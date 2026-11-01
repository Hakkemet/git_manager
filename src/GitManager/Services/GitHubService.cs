using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GitManager.Models;

namespace GitManager.Services;

public class GitHubService
{
    private readonly HttpClient _http;
    private readonly ConfigService _configService;

    public GitHubService(ConfigService configService)
    {
        _configService = configService;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitManager", "1.0.0"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    private void ConfigureAuthHeader()
    {
        _http.DefaultRequestHeaders.Authorization = null;
        var token = _configService.Config.GitHubToken?.Trim();
        if (!string.IsNullOrEmpty(token))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }

    public static bool TryParseGitHubUrl(string input, out string owner, out string repo)
    {
        owner = string.Empty;
        repo = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var trimmed = input.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^4];

        // Suporta https://github.com/owner/repo ou apenas owner/repo
        var match = Regex.Match(trimmed, @"(?:https?://github\.com/|git@github\.com:)?([^/\s]+)/([^/\s#?]+)");
        if (!match.Success) return false;

        owner = match.Groups[1].Value;
        repo = match.Groups[2].Value;
        return !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo);
    }

    public async Task<(bool Updated, string Message)> SyncRepositoryAsync(RepositoryItem item, bool forceDownload = false, CancellationToken ct = default)
    {
        if (!TryParseGitHubUrl(item.RepoUrl, out var owner, out var repoName))
            return (false, "URL do GitHub inválida.");

        item.IsUpdating = true;
        item.StatusMessage = LocalizationService.Instance["StatusCheckingGitHub"];

        try
        {
            ConfigureAuthHeader();
            item.Owner = owner;
            item.Name = repoName;

            // 1. Metadados do repositório
            var repoApiUrl = $"https://api.github.com/repos/{owner}/{repoName}";
            using var repoResp = await _http.GetAsync(repoApiUrl, ct);
            if (!repoResp.IsSuccessStatusCode)
            {
                var statusMsg = repoResp.StatusCode == System.Net.HttpStatusCode.NotFound
                    ? "Repositório não encontrado (404)."
                    : $"Erro HTTP {(int)repoResp.StatusCode}";
                item.StatusMessage = statusMsg;
                return (false, statusMsg);
            }

            var repoJson = await repoResp.Content.ReadAsStringAsync(ct);
            using var repoDoc = JsonDocument.Parse(repoJson);
            var root = repoDoc.RootElement;

            var description = root.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String
                ? descProp.GetString()
                : null;
            item.Description = string.IsNullOrWhiteSpace(description)
                ? "Sem descrição fornecida no repositório original."
                : description!;

            if (root.TryGetProperty("default_branch", out var branchProp) && branchProp.ValueKind == JsonValueKind.String)
                item.DefaultBranch = branchProp.GetString() ?? "main";

            if (root.TryGetProperty("language", out var langProp) && langProp.ValueKind == JsonValueKind.String)
                item.Language = langProp.GetString() ?? "Git";

            if (root.TryGetProperty("stargazers_count", out var starsProp) && starsProp.TryGetInt32(out var stars))
                item.StarsCount = stars;

            // 2. Busca última Release ou Tag (se existir) e últimos Commits para notas de atualização
            string versionTag = string.Empty;
            string releaseBody = string.Empty;

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

            // 3. Busca os últimos commits da branch padrão para obter o SHA atual e histórico recente
            string latestSha = string.Empty;
            var commitsBuilder = new StringBuilder();
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

            if (string.IsNullOrWhiteSpace(versionTag))
                versionTag = item.DefaultBranch;

            var combinedNotes = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(releaseBody))
            {
                combinedNotes.AppendLine(releaseBody);
                combinedNotes.AppendLine();
                combinedNotes.AppendLine("--- Últimos Commits ---");
            }
            if (commitsBuilder.Length > 0)
            {
                combinedNotes.Append(commitsBuilder.ToString().TrimEnd());
            }

            var finalNotes = combinedNotes.Length > 0
                ? combinedNotes.ToString()
                : "Repositório sincronizado com sucesso.";

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

            // 4. Faz o download do repositório completo em formato .zip direto para a pasta do repositório em GitManagerLibrary
            item.StatusMessage = LocalizationService.Instance["StatusDownloadingZip"];
            var safeTag = SanitizeToken(versionTag);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var zipFileName = $"{SanitizeToken(repoName)}_{timestamp}_{safeTag}_{shortLatestSha}.zip";
            var zipFullPath = Path.Combine(repoFolder, zipFileName);

            var archiveUrl = $"https://api.github.com/repos/{owner}/{repoName}/zipball/{Uri.EscapeDataString(item.DefaultBranch)}";
            using (var zipResp = await _http.GetAsync(archiveUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!zipResp.IsSuccessStatusCode)
                {
                    // Fallback para URL pública de archive do GitHub
                    var fallbackUrl = $"https://github.com/{owner}/{repoName}/archive/refs/heads/{Uri.EscapeDataString(item.DefaultBranch)}.zip";
                    using var fallbackResp = await _http.GetAsync(fallbackUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                    fallbackResp.EnsureSuccessStatusCode();
                    await using var fs = new FileStream(zipFullPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await fallbackResp.Content.CopyToAsync(fs, ct);
                }
                else
                {
                    await using var fs = new FileStream(zipFullPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await zipResp.Content.CopyToAsync(fs, ct);
                }
            }

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
            item.StatusMessage = $"Nova versão salva ({newVersion.FormattedSize})";

            return (true, $"Nova versão {shortLatestSha} salva em .zip!");
        }
        catch (Exception ex)
        {
            item.StatusMessage = $"Erro: {ex.Message}";
            return (false, ex.Message);
        }
        finally
        {
            item.IsUpdating = false;
        }
    }

    /// <summary>
    /// Cria um novo projeto extraindo o arquivo .zip selecionado para a pasta destino escolhida pelo usuário,
    /// sem alterar nem modificar o arquivo .zip original armazenado em GitManagerLibrary.
    /// </summary>
    public Task CreateProjectFromZipAsync(ZipVersionEntry versionEntry, string destinationFolder)
    {
        return Task.Run(() =>
        {
            if (!File.Exists(versionEntry.FullPath))
                throw new FileNotFoundException("O arquivo .zip desta versão não foi encontrado na biblioteca.", versionEntry.FullPath);

            Directory.CreateDirectory(destinationFolder);

            using var archive = ZipFile.OpenRead(versionEntry.FullPath);
            // Os arquivos zipball do GitHub geralmente possuem uma pasta raiz única (ex: owner-repo-sha/).
            // Removemos esse prefixo raiz para extrair os arquivos limpos diretamente na pasta do projeto escolhida.
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
                // Proteção contra Zip Slip
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
