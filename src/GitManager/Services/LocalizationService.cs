using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace GitManager.Services;

public class LocalizationService : INotifyPropertyChanged
{
    public const string DefaultLanguage = "en_US";

    public static LocalizationService Instance { get; } = new();

    private Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private string _currentLanguage = DefaultLanguage;

    public string CurrentLanguage => _currentLanguage;

    public string this[string key] => Get(key);

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? LanguageChanged;

    private LocalizationService()
    {
        LoadLanguage(DefaultLanguage);
    }

    public void LoadLanguage(string? langCode, string? baseDirectory = null)
    {
        var normalized = string.IsNullOrWhiteSpace(langCode) ? DefaultLanguage : langCode.Trim();
        if (!normalized.Equals("en_US", StringComparison.OrdinalIgnoreCase) &&
            !normalized.Equals("pt_BR", StringComparison.OrdinalIgnoreCase))
        {
            normalized = DefaultLanguage;
        }

        _currentLanguage = normalized;

        var loaded = TryLoadFromDisk(normalized, baseDirectory) ?? TryLoadFromEmbedded(normalized) ?? new Dictionary<string, string>();
        _strings = new Dictionary<string, string>(loaded, StringComparer.OrdinalIgnoreCase);

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        LanguageChanged?.Invoke();
    }

    public string Get(string key)
    {
        if (_strings.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
            return val;
        return key;
    }

    public string Format(string key, params object[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch
        {
            return template;
        }
    }

    private static Dictionary<string, string>? TryLoadFromDisk(string langCode, string? baseDir)
    {
        try
        {
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(baseDir)) roots.Add(baseDir);
            roots.Add(AppContext.BaseDirectory);

            foreach (var root in roots)
            {
                var candidate1 = Path.Combine(root, "Locales", $"{langCode}.json");
                if (File.Exists(candidate1))
                {
                    var json = File.ReadAllText(candidate1);
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                }

                var candidate2 = Path.Combine(root, "src", "GitManager", "Locales", $"{langCode}.json");
                if (File.Exists(candidate2))
                {
                    var json = File.ReadAllText(candidate2);
                    return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                }
            }
        }
        catch
        {
            // Ignora e usa fallback embutido
        }
        return null;
    }

    private static Dictionary<string, string>? TryLoadFromEmbedded(string langCode)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = $"GitManager.Locales.{langCode}.json";
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) return null;
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        }
        catch
        {
            return null;
        }
    }
}
