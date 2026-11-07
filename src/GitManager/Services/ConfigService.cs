using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using GitManager.Models;
using Microsoft.Win32;

namespace GitManager.Services;

public class ConfigService
{
    private const string RegistryRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryName = "GitManager";
    // Manifesto salvo dentro de cada pasta de repositório: permite restaurar a biblioteca só copiando as pastas
    public const string RepoManifestName = ".gitmanager.json";

    public string BaseDirectory { get; }
    public string ConfigFilePath { get; }
    public AppConfig Config { get; private set; }

    public ConfigService()
    {
        BaseDirectory = ResolveBaseDirectory();
        ConfigFilePath = Path.Combine(BaseDirectory, "config.json");
        Config = LoadOrCreateConfig();
        LocalizationService.Instance.LoadLanguage(Config.Language, BaseDirectory);
        EnsureLibraryDirectories();
        SyncWindowsStartup(Config.StartWithWindows);
    }

    private static string ResolveBaseDirectory()
    {
        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        // Se estiver rodando dentro de bin/Debug ou bin/Release durante desenvolvimento, sobe até a raiz do projeto para manter a pasta GitManagerLibrary acessível
        var dirInfo = new DirectoryInfo(exeDir);
        if (dirInfo.Name.StartsWith("net", StringComparison.OrdinalIgnoreCase) &&
            dirInfo.Parent?.Name is "Debug" or "Release" &&
            dirInfo.Parent?.Parent?.Name == "bin")
        {
            var projectRoot = dirInfo.Parent.Parent.Parent?.Parent?.FullName ?? dirInfo.Parent.Parent.Parent?.FullName;
            if (!string.IsNullOrEmpty(projectRoot))
                return projectRoot;
        }
        return exeDir;
    }

    private AppConfig LoadOrCreateConfig()
    {
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var json = File.ReadAllText(ConfigFilePath);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json);
                if (cfg != null)
                {
                    if (string.IsNullOrWhiteSpace(cfg.Language))
                        cfg.Language = LocalizationService.DefaultLanguage;
                    if (string.IsNullOrWhiteSpace(cfg.LibraryPath))
                        cfg.LibraryPath = Path.Combine(BaseDirectory, "GitManagerLibrary");
                    return cfg;
                }
            }
        }
        catch
        {
            // Se houver falha de leitura, recria config padrão
        }

        var defaultConfig = new AppConfig
        {
            Language = LocalizationService.DefaultLanguage,
            LibraryPath = Path.Combine(BaseDirectory, "GitManagerLibrary"),
            CheckIntervalMinutes = 30,
            StartWithWindows = true,
            MinimizeToTrayOnClose = true,
            AutoUpdateApp = true
        };
        SaveConfig(defaultConfig);
        return defaultConfig;
    }

    public void SaveConfig(AppConfig? config = null)
    {
        if (config != null)
            Config = config;

        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(Config, options);
            File.WriteAllText(ConfigFilePath, json);
            WriteRepositoryManifests(options);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Erro ao salvar config: {ex.Message}");
        }
    }

    private void WriteRepositoryManifests(JsonSerializerOptions options)
    {
        foreach (var repo in Config.Repositories)
        {
            try
            {
                var folder = GetRepositoryFolder(repo);
                if (!Directory.Exists(folder)) continue;
                var path = Path.Combine(folder, RepoManifestName);
                var json = JsonSerializer.Serialize(repo, options);
                if (File.Exists(path) && File.ReadAllText(path) == json) continue;
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Aviso ao salvar manifesto: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Adiciona à config os repositórios que existem como pasta na biblioteca mas não estão cadastrados
    /// (biblioteca copiada de backup / após formatar). Usa o manifesto se existir, senão o nome "owner__repo".
    /// </summary>
    public int ImportRepositoriesFromLibrary()
    {
        if (!Directory.Exists(Config.LibraryPath)) return 0;
        int imported = 0;

        foreach (var dir in Directory.GetDirectories(Config.LibraryPath))
        {
            try
            {
                RepositoryItem? repo = null;
                var manifest = Path.Combine(dir, RepoManifestName);
                if (File.Exists(manifest))
                    repo = JsonSerializer.Deserialize<RepositoryItem>(File.ReadAllText(manifest));

                if (repo == null || string.IsNullOrWhiteSpace(repo.Name))
                {
                    var folderName = Path.GetFileName(dir);
                    var sep = folderName.IndexOf("__", StringComparison.Ordinal);
                    if (sep <= 0 || sep + 2 >= folderName.Length) continue; // não é pasta de repositório
                    if (!Directory.EnumerateFiles(dir, "*.zip").Any()) continue;
                    repo = new RepositoryItem
                    {
                        Owner = folderName[..sep],
                        Name = folderName[(sep + 2)..],
                        Description = string.Empty
                    };
                }

                if (string.IsNullOrWhiteSpace(repo.RepoUrl))
                    repo.RepoUrl = $"https://github.com/{repo.Owner}/{repo.Name}";

                bool exists = Config.Repositories.Any(r =>
                    string.Equals(r.Owner, repo.Owner, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Name, repo.Name, StringComparison.OrdinalIgnoreCase));
                if (exists) continue;

                // Instalação vinculada é específica do PC: após restaurar, o usuário vincula de novo
                if (!string.IsNullOrWhiteSpace(repo.InstallPath) && !Directory.Exists(repo.InstallPath))
                {
                    repo.InstallPath = string.Empty;
                    repo.InstalledVersion = string.Empty;
                }

                Config.Repositories.Add(repo);
                imported++;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Aviso ao importar {dir}: {ex.Message}");
            }
        }
        return imported;
    }

    public void EnsureLibraryDirectories()
    {
        if (string.IsNullOrWhiteSpace(Config.LibraryPath))
            Config.LibraryPath = Path.Combine(BaseDirectory, "GitManagerLibrary");

        Directory.CreateDirectory(Config.LibraryPath);

        if (Config.Categories == null || Config.Categories.Count == 0)
        {
            Config.Categories = new()
            {
                "Geral",
                "IA / Agentes",
                "Jogos / Recomp",
                "Engenharia Reversa",
                "Ferramentas",
                "Web / Frontend",
                "Backend / API"
            };
        }

        // Usuário apontou para a pasta que CONTÉM a biblioteca: desce para GitManagerLibrary
        var nested = Path.Combine(Config.LibraryPath, "GitManagerLibrary");
        if (Directory.Exists(nested) &&
            !Directory.GetDirectories(Config.LibraryPath).Any(d => Path.GetFileName(d).Contains("__")))
        {
            Config.LibraryPath = nested;
            SaveConfig();
        }

        if (ImportRepositoriesFromLibrary() > 0)
            SaveConfig();

        foreach (var repo in Config.Repositories)
        {
            if (string.IsNullOrWhiteSpace(repo.Category))
                repo.Category = "Geral";
            if (!Config.Categories.Any(c => string.Equals(c, repo.Category, StringComparison.OrdinalIgnoreCase)))
                Config.Categories.Add(repo.Category);

            var repoFolder = GetRepositoryFolder(repo);
            repo.FolderPath = repoFolder;
            Directory.CreateDirectory(repoFolder);
            ReconcileFolderZipFiles(repo);
        }
    }

    public string GetRepositoryFolder(RepositoryItem repo)
    {
        var safeName = string.IsNullOrWhiteSpace(repo.Owner)
            ? SanitizeFileName(repo.Name)
            : $"{SanitizeFileName(repo.Owner)}__{SanitizeFileName(repo.Name)}";

        return Path.Combine(Config.LibraryPath, safeName);
    }

    public void ReconcileFolderZipFiles(RepositoryItem repo)
    {
        var folder = GetRepositoryFolder(repo);
        repo.FolderPath = folder;
        if (!Directory.Exists(folder))
            return;

        // Biblioteca movida/restaurada: o .zip está na pasta atual com o mesmo nome, só o caminho antigo mudou
        foreach (var v in repo.Versions)
        {
            var local = Path.Combine(folder, v.FileName);
            if (!string.IsNullOrWhiteSpace(v.FileName) && File.Exists(local))
                v.FullPath = local;
        }

        // Entradas importadas antes guardavam o nome do arquivo inteiro como tag
        foreach (var v in repo.Versions)
        {
            if (string.Equals(v.VersionTag, Path.GetFileNameWithoutExtension(v.FileName), StringComparison.OrdinalIgnoreCase))
                v.VersionTag = TagFromZipName(v.FileName);
        }

        // Remove da lista entradas cujo arquivo .zip foi deletado manualmente
        var existingEntries = repo.Versions.Where(v => File.Exists(v.FullPath)).ToList();

        // Descobre arquivos .zip na pasta que ainda não estejam na lista
        var zipFiles = Directory.GetFiles(folder, "*.zip")
            .OrderByDescending(File.GetCreationTime)
            .ToList();

        foreach (var zipPath in zipFiles)
        {
            if (!existingEntries.Any(e => string.Equals(e.FullPath, zipPath, StringComparison.OrdinalIgnoreCase)))
            {
                var fi = new FileInfo(zipPath);
                existingEntries.Add(new ZipVersionEntry
                {
                    FileName = fi.Name,
                    FullPath = fi.FullName,
                    VersionTag = TagFromZipName(fi.Name),
                    CommitSha = System.Text.RegularExpressions.Regex.Match(fi.Name, @"_([0-9a-f]{7,40})\.zip$") is { Success: true } m ? m.Groups[1].Value : "local",
                    ReleaseNotes = "Arquivo .zip encontrado diretamente na pasta do repositório.",
                    // Cópia/backup reseta a data do arquivo; o nome guarda a data real do download
                    CreatedAt = System.Text.RegularExpressions.Regex.Match(fi.Name, @"_(\d{8}_\d{6})_") is { Success: true } dm &&
                                DateTime.TryParseExact(dm.Groups[1].Value, "yyyyMMdd_HHmmss", null, System.Globalization.DateTimeStyles.None, out var named)
                        ? named
                        : fi.CreationTime,
                    FileSizeBytes = fi.Length
                });
            }
        }

        repo.Versions.Clear();
        foreach (var item in existingEntries.OrderByDescending(x => x.CreatedAt))
        {
            repo.Versions.Add(item);
        }
        if (repo.Versions.Count > 0 && string.IsNullOrWhiteSpace(repo.LatestCommitSha) && repo.Versions[0].CommitSha != "local")
            repo.LatestCommitSha = repo.Versions[0].CommitSha;
        repo.NotifyVersionsChanged();
    }

    /// <summary>"repo_20261107_054812_nx2_0857e8b.zip" → "nx2"; nome fora do padrão → nome sem extensão.</summary>
    public static string TagFromZipName(string fileName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(fileName, @"_\d{8}_\d{6}_(.+)_[0-9a-f]{7,40}\.zip$");
        return m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(fileName);
    }

    public void SyncWindowsStartup(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, writable: true);
            if (key == null) return;

            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(exePath) && exePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    // Se o executável não for o dotnet.exe genérico, registra para iniciar com --minimized
                    if (!Path.GetFileName(exePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        key.SetValue(AppRegistryName, $"\"{exePath}\" --tray");
                    }
                }
            }
            else
            {
                if (key.GetValue(AppRegistryName) != null)
                    key.DeleteValue(AppRegistryName, false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Aviso ao registrar inicialização do Windows: {ex.Message}");
        }
    }

    private static string SanitizeFileName(string input)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            input = input.Replace(c, '_');
        return input.Trim();
    }
}
