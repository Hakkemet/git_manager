using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using GitManager.Models;

namespace GitManager.Services;

public class GitHubService
{
    private readonly HttpClient _http;
    private readonly ConfigService _configService;
    private string? _cachedSystemGitToken;
    private bool _probedSystemGitToken;

    public GitHubService(ConfigService configService)
    {
        _configService = configService;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitManager", "1.1.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    private void ConfigureAuthHeader()
    {
        _http.DefaultRequestHeaders.Authorization = null;
        var token = _configService.Config.GitHubToken?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            token = TryGetWindowsGitCredentialToken();
        }
        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public string? TryGetWindowsGitCredentialToken()
    {
        if (_probedSystemGitToken)
            return _cachedSystemGitToken;

        _probedSystemGitToken = true;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "credential-manager get",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            proc.StandardInput.Write("protocol=https\nhost=github.com\n\n");
            proc.StandardInput.Close();

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("password=", StringComparison.OrdinalIgnoreCase))
                {
                    _cachedSystemGitToken = trimmed["password=".Length..].Trim();
                    break;
                }
            }
        }
        catch
        {
            // Se não encontrar o Git Credential Manager, segue sem token
        }
        return _cachedSystemGitToken;
    }

    public static bool TryParseGitHubUrl(string input, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        var match = Regex.Match(trimmed, @"(?:https?://github\.com/|git@github\.com:)?([^/\s]+)/([^/\s#?]+)");
        if (!match.Success) return false;

        owner = match.Groups[1].Value;
        repo = match.Groups[2].Value;
        return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
    }

    public async Task<(bool Updated, string Message)> SyncRepositoryAsync(RepositoryItem item, bool forceDownload = false, CancellationToken ct = default)
    {
        if (!TryParseGitHubUrl(item.RepoUrl, out var owner, out var repoName))
            return (false, "Invalid GitHub URL.");

        item.IsUpdating = true;
        item.StatusMessage = LocalizationService.Instance["StatusCheckingGitHub"];

        try
        {
            ConfigureAuthHeader();
            item.Owner = owner;
            item.Name = repoName;

            bool usedFallback = false;
            string versionTag = string.Empty;
            string releaseBody = string.Empty;
            string latestSha = string.Empty;
            var commitsBuilder = new StringBuilder();

            // 1. Tenta via API do GitHub primeiro; se retornar 403/429 (Rate Limit), faz fallback automático 100% público sem limite!
            var repoApiUrl = $"https://api.github.com/repos/{owner}/{repoName}";
            using var repoResp = await _http.GetAsync(repoApiUrl, ct);

            if (repoResp.StatusCode == HttpStatusCode.NotFound)
            {
                item.StatusMessage = "404 Not Found";
                return (false, "Repository not found (404).");
            }

            if (repoResp.IsSuccessStatusCode)
            {
                var repoJson = await repoResp.Content.ReadAsStringAsync(ct);
                using var repoDoc = JsonDocument.Parse(repoJson);
                var root = repoDoc.RootElement;

                var description = root.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String
                    ? descProp.GetString()
                    : null;
                if (!string.IsNullOrWhiteSpace(description))
                    item.Description = description!;
                else if (string.IsNullOrWhiteSpace(item.Description) || item.Description.Contains("GitHub..."))
                    item.Description = "No description provided.";

                if (root.TryGetProperty("default_branch", out var branchProp) && branchProp.ValueKind == JsonValueKind.String)
                    item.DefaultBranch = branchProp.GetString() ?? "main";

                if (root.TryGetProperty("language", out var langProp) && langProp.ValueKind == JsonValueKind.String)
                    item.Language = langProp.GetString() ?? "Git";

                if (root.TryGetProperty("stargazers_count", out var starsProp) && starsProp.TryGetInt32(out var stars))
                    item.StarsCount = stars;

                // Release via API
                var releaseUrl = $"https://api.github.com/repos/{owner}/{repoName}/releases/latest";
                using (var relResp = await _http.GetAsync(releaseUrl, ct))
                {
                    if (relResp.IsSuccessStatusCode)
                    {
                        var relJson = await relResp.Content.ReadAsStringAsync(ct);
                        using var relDoc = JsonDocument.Parse(relJson);
                        var relRoot = relDoc.RootElement;
                        if (relRoot.TryGetProperty("tag_name", out var tagProp) && tagProp.ValueKind == JsonValueKind.String)
                            versionTag = tagProp.GetString() ?? string.Empty;

                        var relName = relRoot.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String
                            ? nameProp.GetString()
                            : string.Empty;

                        var body = relRoot.TryGetProperty("body", out var bodyProp) && bodyProp.ValueKind == JsonValueKind.String
                            ? bodyProp.GetString()
                            : string.Empty;

                        if (!string.IsNullOrWhiteSpace(body))
                            releaseBody = $"[Release {versionTag}] {relName}\n{body.Trim()}";
                    }
                }

                // Commits via API
                var commitsUrl = $"https://api.github.com/repos/{owner}/{repoName}/commits?sha={Uri.EscapeDataString(item.DefaultBranch)}&per_page=6";
                using (var commitsResp = await _http.GetAsync(commitsUrl, ct))
                {
                    if (commitsResp.IsSuccessStatusCode)
                    {
                        var commitsJson = await commitsResp.Content.ReadAsStringAsync(ct);
                        using var commitsDoc = JsonDocument.Parse(commitsJson);
                        if (commitsDoc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            int idx = 0;
                            foreach (var c in commitsDoc.RootElement.EnumerateArray())
                            {
                                var sha = c.TryGetProperty("sha", out var sProp) ? sProp.GetString() ?? "" : "";
                                if (idx == 0) latestSha = sha;

                                var shortSha = sha.Length >= 7 ? sha[..7] : sha;
                                string msg = "";
                                string dateStr = "";
                                if (c.TryGetProperty("commit", out var commitObj))
                                {
                                    if (commitObj.TryGetProperty("message", out var mProp))
                                        msg = (mProp.GetString() ?? "").Split('\n')[0].Trim();
                                    if (commitObj.TryGetProperty("author", out var aProp) &&
                                        aProp.TryGetProperty("date", out var dProp) &&
                                        DateTime.TryParse(dProp.GetString(), out var dt))
                                    {
                                        dateStr = dt.ToLocalTime().ToString("dd/MM HH:mm");
                                    }
                                }
                                commitsBuilder.AppendLine($"• [{shortSha}] ({dateStr}) {msg}");
                                idx++;
                            }
                        }
                    }
                }
            }
            else
            {
                // FALLBACK PÚBLICO ILIMITADO (Sem consumir Rate Limit de 60 req/h da API do GitHub!)
                usedFallback = true;
                await PopulateFromPublicWebAndAtomAsync(item, owner, repoName, commitsBuilder, ct);
                versionTag = item.LatestVersionTag;
                latestSha = item.LatestCommitSha;
            }

            if (string.IsNullOrWhiteSpace(versionTag))
                versionTag = string.IsNullOrWhiteSpace(item.DefaultBranch) ? "main" : item.DefaultBranch;

            var combinedNotes = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(releaseBody))
            {
                combinedNotes.AppendLine(releaseBody);
                combinedNotes.AppendLine();
                combinedNotes.AppendLine("--- Commits ---");
            }
            if (commitsBuilder.Length > 0)
            {
                combinedNotes.Append(commitsBuilder.ToString().TrimEnd());
            }

            var finalNotes = combinedNotes.Length > 0
                ? combinedNotes.ToString()
                : (string.IsNullOrWhiteSpace(item.LatestUpdateNotes) ? "Repository synced successfully." : item.LatestUpdateNotes);

            item.LastCheckedAt = DateTime.Now;
            item.LatestVersionTag = versionTag;
            item.LatestUpdateNotes = finalNotes;

            // Verifica se já temos um arquivo .zip para este mesmo commit SHA na pasta do repositório
            var repoFolder = _configService.GetRepositoryFolder(item);
            item.FolderPath = repoFolder;
            Directory.CreateDirectory(repoFolder);

            var shortLatestSha = latestSha.Length >= 7 ? latestSha[..7] : "latest";
            bool alreadyHasCommitZip = !forceDownload &&
                !string.IsNullOrWhiteSpace(latestSha) &&
                item.Versions.Any(v =>
                    (string.Equals(v.CommitSha, latestSha, StringComparison.OrdinalIgnoreCase) ||
                     v.FileName.Contains(shortLatestSha, StringComparison.OrdinalIgnoreCase)) &&
                    File.Exists(v.FullPath));

            if (alreadyHasCommitZip)
            {
                item.LatestCommitSha = latestSha;
                item.StatusMessage = LocalizationService.Instance["StatusUpToDate"];
                return (false, LocalizationService.Instance["StatusUpToDate"]);
            }

            // Se não conseguimos descobrir um SHA novo e já existe pelo menos um .zip salvo e não é forceDownload, mantém atualizado
            if (!forceDownload && string.IsNullOrWhiteSpace(latestSha) && item.Versions.Any(v => File.Exists(v.FullPath)))
            {
                item.StatusMessage = LocalizationService.Instance["StatusUpToDate"];
                return (false, LocalizationService.Instance["StatusUpToDate"]);
            }

            // 4. Faz o download do repositório completo em formato .zip direto para a pasta do repositório em GitManagerLibrary
            item.StatusMessage = LocalizationService.Instance["StatusDownloadingZip"];
            var safeTag = SanitizeToken(versionTag);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var zipFileName = $"{SanitizeToken(repoName)}_{timestamp}_{safeTag}_{shortLatestSha}.zip";
            var zipFullPath = Path.Combine(repoFolder, zipFileName);

            bool downloaded = false;
            if (!usedFallback)
            {
                var archiveUrl = $"https://api.github.com/repos/{owner}/{repoName}/zipball/{Uri.EscapeDataString(item.DefaultBranch)}";
                using var zipResp = await _http.GetAsync(archiveUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                if (zipResp.IsSuccessStatusCode)
                {
                    await using var fs = new FileStream(zipFullPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await zipResp.Content.CopyToAsync(fs, ct);
                    downloaded = true;
                }
            }

            if (!downloaded)
            {
                // URL direta pública do GitHub (não tem limite de 60 requisições/hora!)
                var branchesToTry = new[] { item.DefaultBranch, "main", "master" }.Distinct(StringComparer.OrdinalIgnoreCase);
                foreach (var branch in branchesToTry)
                {
                    if (string.IsNullOrWhiteSpace(branch)) continue;
                    var publicZipUrl = $"https://github.com/{owner}/{repoName}/archive/refs/heads/{Uri.EscapeDataString(branch)}.zip";
                    using var pubResp = await _http.GetAsync(publicZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (pubResp.IsSuccessStatusCode)
                    {
                        item.DefaultBranch = branch;
                        await using var fs = new FileStream(zipFullPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        await pubResp.Content.CopyToAsync(fs, ct);
                        downloaded = true;
                        break;
                    }
                }
            }

            if (!downloaded)
                throw new InvalidOperationException("Could not download .zip archive from GitHub.");

            var fileInfo = new FileInfo(zipFullPath);
            var newVersion = new ZipVersionEntry
            {
                FileName = zipFileName,
                FullPath = zipFullPath,
                VersionTag = versionTag,
                CommitSha = string.IsNullOrWhiteSpace(latestSha) ? shortLatestSha : latestSha,
                ReleaseNotes = finalNotes,
                CreatedAt = DateTime.Now,
                FileSizeBytes = fileInfo.Length
            };

            item.LatestCommitSha = newVersion.CommitSha;
            item.LastUpdatedAt = DateTime.Now;
            item.Versions.Insert(0, newVersion);
            item.SelectedVersion = newVersion;
            item.NotifyVersionsChanged();
            item.StatusMessage = LocalizationService.Instance["StatusUpToDate"];

            return (true, $"New .zip version ({shortLatestSha}) saved!");
        }
        catch (Exception ex)
        {
            item.StatusMessage = $"Error: {ex.Message}";
            return (false, ex.Message);
        }
        finally
        {
            item.IsUpdating = false;
        }
    }

    private async Task PopulateFromPublicWebAndAtomAsync(
        RepositoryItem item,
        string owner,
        string repoName,
        StringBuilder commitsBuilder,
        CancellationToken ct)
    {
        try
        {
            // 1. Extrai descrição da meta tag pública og:description da página do repositório
            var pageUrl = $"https://github.com/{owner}/{repoName}";
            using var pageResp = await _http.GetAsync(pageUrl, ct);
            if (pageResp.IsSuccessStatusCode)
            {
                var html = await pageResp.Content.ReadAsStringAsync(ct);
                var descMatch = Regex.Match(html, @"<meta\s+property=""og:description""\s+content=""([^""]+)""", RegexOptions.IgnoreCase);
                if (descMatch.Success)
                {
                    var rawDesc = WebUtility.HtmlDecode(descMatch.Groups[1].Value).Trim();
                    var suffixIdx = rawDesc.LastIndexOf($" - {owner}/{repoName}", StringComparison.OrdinalIgnoreCase);
                    if (suffixIdx > 0)
                        rawDesc = rawDesc[..suffixIdx].Trim();
                    if (!string.IsNullOrWhiteSpace(rawDesc))
                        item.Description = rawDesc;
                }
            }
        }
        catch
        {
            // Ignora falha ao ler página HTML
        }

        try
        {
            // 2. Lê feed público Atom de Commits (não consome API rate limit)
            var feedUrl = $"https://github.com/{owner}/{repoName}/commits.atom";
            using var feedResp = await _http.GetAsync(feedUrl, ct);
            if (feedResp.IsSuccessStatusCode)
            {
                var xml = await feedResp.Content.ReadAsStringAsync(ct);
                var doc = XDocument.Parse(xml);
                XNamespace ns = "http://www.w3.org/2005/Atom";
                int idx = 0;
                foreach (var entry in doc.Descendants(ns + "entry").Take(6))
                {
                    var idVal = entry.Element(ns + "id")?.Value ?? "";
                    var shaMatch = Regex.Match(idVal, @"Commit/([0-9a-fA-F]{7,40})");
                    var sha = shaMatch.Success ? shaMatch.Groups[1].Value : "";
                    if (idx == 0 && !string.IsNullOrEmpty(sha))
                        item.LatestCommitSha = sha;

                    var shortSha = sha.Length >= 7 ? sha[..7] : "commit";
                    var title = entry.Element(ns + "title")?.Value?.Trim() ?? "";
                    var updatedStr = entry.Element(ns + "updated")?.Value ?? "";
                    var dateFormatted = DateTime.TryParse(updatedStr, out var dt)
                        ? dt.ToLocalTime().ToString("dd/MM HH:mm")
                        : "";

                    commitsBuilder.AppendLine($"• [{shortSha}] ({dateFormatted}) {title}");
                    idx++;
                }
            }
        }
        catch
        {
            // Ignora falha no Atom feed
        }

        if (string.IsNullOrWhiteSpace(item.LatestVersionTag))
            item.LatestVersionTag = string.IsNullOrWhiteSpace(item.DefaultBranch) ? "main" : item.DefaultBranch;
    }

    public Task CreateProjectFromZipAsync(ZipVersionEntry versionEntry, string destinationFolder)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(versionEntry.FullPath))
                throw new FileNotFoundException("The .zip archive for this version was not found in the library.", versionEntry.FullPath);

            Directory.CreateDirectory(destinationFolder);

            using var archive = ZipFile.OpenRead(versionEntry.FullPath);
            string? commonRootPrefix = DetectSingleRootPrefix(archive);

            foreach (var entry in archive.Entries)
            {
                var relativePath = entry.FullName;
                if (!string.IsNullOrEmpty(commonRootPrefix) && relativePath.StartsWith(commonRootPrefix, StringComparison.Ordinal))
                {
                    relativePath = relativePath[commonRootPrefix.Length..];
                }

                if (string.IsNullOrWhiteSpace(relativePath))
                    continue;

                var targetPath = Path.GetFullPath(Path.Combine(destinationFolder, relativePath));
                var fullDestRoot = Path.GetFullPath(destinationFolder);
                if (!targetPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(targetPath);
                }
                else
                {
                    var parentDir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(parentDir))
                        Directory.CreateDirectory(parentDir);

                    entry.ExtractToFile(targetPath, overwrite: true);
                }
            }
        });
    }

    private static string? DetectSingleRootPrefix(ZipArchive archive)
    {
        string? rootPrefix = null;
        foreach (var entry in archive.Entries)
        {
            var slashIdx = entry.FullName.IndexOf('/');
            if (slashIdx <= 0) return null;

            var candidate = entry.FullName[..(slashIdx + 1)];
            if (rootPrefix == null)
                rootPrefix = candidate;
            else if (!string.Equals(rootPrefix, candidate, StringComparison.Ordinal))
                return null;
        }
        return rootPrefix;
    }

    private static string SanitizeToken(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "v1";
        foreach (var c in Path.GetInvalidFileNameChars())
            input = input.Replace(c, '-');
        return input.Replace(' ', '_');
    }
}
