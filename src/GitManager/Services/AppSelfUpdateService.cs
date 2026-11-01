using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GitManager.Services;

public record AppUpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseNotes,
    string DownloadUrl,
    string AssetName);

public class AppSelfUpdateService
{
    public const string OfficialRepoUrl = "https://github.com/Hakkemet/git_manager";
    public const string OfficialRepoOwner = "Hakkemet";
    public const string OfficialRepoName = "git_manager";

    private readonly ConfigService _configService;
    private readonly HttpClient _http;

    public AppSelfUpdateService(ConfigService configService)
    {
        _configService = configService;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("GitManagerUpdater", CurrentVersion));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    public async Task<AppUpdateCheckResult> CheckForAppUpdateAsync(CancellationToken ct = default)
    {
        const string owner = OfficialRepoOwner;
        const string repo = OfficialRepoName;

        try
        {
            _http.DefaultRequestHeaders.Authorization = null;
            var token = _configService.Config.GitHubToken?.Trim();
            if (!string.IsNullOrEmpty(token))
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var apiUrl = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            using var resp = await _http.GetAsync(apiUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return new AppUpdateCheckResult(false, CurrentVersion, CurrentVersion, "Nenhuma release publicada ainda no repositório do app.", "", "");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var tProp) ? tProp.GetString() ?? "" : "";
            var cleanTag = tagName.TrimStart('v', 'V');
            var notes = root.TryGetProperty("body", out var bProp) ? bProp.GetString() ?? "" : "";

            string downloadUrl = "";
            string assetName = "";

            if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsProp.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                    var url = asset.TryGetProperty("browser_download_url", out var uProp) ? uProp.GetString() ?? "" : "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        assetName = name;
                        downloadUrl = url;
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            break; // Prioriza pacote portátil completo .zip
                    }
                }
            }

            bool isNewer = IsVersionNewer(CurrentVersion, cleanTag);
            return new AppUpdateCheckResult(isNewer && !string.IsNullOrEmpty(downloadUrl), CurrentVersion, string.IsNullOrEmpty(cleanTag) ? CurrentVersion : cleanTag, notes, downloadUrl, assetName);
        }
        catch (Exception ex)
        {
            return new AppUpdateCheckResult(false, CurrentVersion, CurrentVersion, $"Erro ao verificar atualização: {ex.Message}", "", "");
        }
    }

    public async Task<(bool Success, string Message)> DownloadAndApplyUpdateAsync(AppUpdateCheckResult updateInfo, CancellationToken ct = default)
    {
        try
        {
            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || Path.GetFileName(currentExe).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                return (false, "A auto-substituição do executável funciona quando o aplicativo é aberto pelo GitManager.exe compilado.");
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "GitManager_Update_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tempDir);

            var downloadedFile = Path.Combine(tempDir, updateInfo.AssetName);
            using (var resp = await _http.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = new FileStream(downloadedFile, FileMode.Create, FileAccess.Write, FileShare.None);
                await resp.Content.CopyToAsync(fs, ct);
            }

            string newExePath = downloadedFile;
            string? extractedDir = null;
            if (downloadedFile.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                extractedDir = Path.Combine(tempDir, "extracted");
                ZipFile.ExtractToDirectory(downloadedFile, extractedDir, overwriteFiles: true);
                var foundExes = Directory.GetFiles(extractedDir, "GitManager.exe", SearchOption.AllDirectories);
                if (foundExes.Length == 0)
                    foundExes = Directory.GetFiles(extractedDir, "*.exe", SearchOption.AllDirectories);

                if (foundExes.Length == 0)
                    return (false, "Nenhum arquivo .exe encontrado no pacote de atualização.");

                newExePath = foundExes[0];
            }

            var targetAppDir = Path.GetDirectoryName(currentExe)!;
            var sourceAppDir = Path.GetDirectoryName(newExePath)!;

            // Cria script .cmd para aguardar o encerramento do processo atual, substituir todos os arquivos da versão e reiniciar
            var batchPath = Path.Combine(tempDir, "apply_update.cmd");
            var copyCommand = extractedDir != null
                ? $"xcopy /E /Y /I \"{sourceAppDir}\\*\" \"{targetAppDir}\\\" >nul"
                : $"copy /Y \"{newExePath}\" \"{currentExe}\" >nul";

            var script = $"""
                @echo off
                timeout /t 2 /nobreak >nul
                {copyCommand}
                start "" "{currentExe}"
                del "%~f0"
                """;
            await File.WriteAllTextAsync(batchPath, script, ct);

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batchPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });

            return (true, "Atualizando e reiniciando...");
        }
        catch (Exception ex)
        {
            return (false, $"Falha ao aplicar atualização: {ex.Message}");
        }
    }

    private static bool IsVersionNewer(string current, string latest)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        if (Version.TryParse(current, out var vCurr) && Version.TryParse(latest, out var vLat))
            return vLat > vCurr;

        return !string.Equals(current, latest, StringComparison.OrdinalIgnoreCase);
    }
}
