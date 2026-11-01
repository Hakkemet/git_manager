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
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Erro ao salvar config: {ex.Message}");
        }
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
                    VersionTag = Path.GetFileNameWithoutExtension(fi.Name),
                    CommitSha = "local",
                    ReleaseNotes = "Arquivo .zip encontrado diretamente na pasta do repositório.",
                    CreatedAt = fi.CreationTime,
                    FileSizeBytes = fi.Length
                });
            }
        }

        repo.Versions.Clear();
        foreach (var item in existingEntries.OrderByDescending(x => x.CreatedAt))
        {
            repo.Versions.Add(item);
        }
        repo.NotifyVersionsChanged();
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
