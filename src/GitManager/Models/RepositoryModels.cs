using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace GitManager.Models;

public class ZipVersionEntry : INotifyPropertyChanged
{
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string VersionTag { get; set; } = string.Empty;
    public string CommitSha { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public long FileSizeBytes { get; set; }

    [JsonIgnore]
    public string FormattedSize => FileSizeBytes switch
    {
        < 1024 => $"{FileSizeBytes} B",
        < 1024 * 1024 => $"{FileSizeBytes / 1024.0:F1} KB",
        _ => $"{FileSizeBytes / (1024.0 * 1024.0):F2} MB"
    };

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(VersionTag)
        ? $"{CommitSha[..Math.Min(7, CommitSha.Length)]} ({CreatedAt:dd/MM/yyyy HH:mm})"
        : $"{VersionTag} • {CommitSha[..Math.Min(7, CommitSha.Length)]} ({CreatedAt:dd/MM/yyyy HH:mm})";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class RepositoryItem : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _owner = string.Empty;
    private string _name = string.Empty;
    private string _repoUrl = string.Empty;
    private string _category = "Geral";
    private string _description = "Carregando descrição do repositório...";
    private string _defaultBranch = "main";
    private string _latestCommitSha = string.Empty;
    private string _latestVersionTag = string.Empty;
    private string _latestUpdateNotes = string.Empty;
    private string _language = "Git";
    private int _starsCount;
    private DateTime _lastCheckedAt = DateTime.Now;
    private DateTime _lastUpdatedAt = DateTime.Now;
    private bool _isUpdating;
    private string _statusMessage = "Pronto";
    private ZipVersionEntry? _selectedVersion;

    public string Id
    {
        get => _id;
        set { _id = value; OnPropertyChanged(); }
    }

    public string Owner
    {
        get => _owner;
        set { _owner = value; OnPropertyChanged(); OnPropertyChanged(nameof(FullName)); }
    }

    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); OnPropertyChanged(nameof(FullName)); }
    }

    [JsonIgnore]
    public string FullName => string.IsNullOrWhiteSpace(Owner) ? Name : $"{Owner} / {Name}";

    public string RepoUrl
    {
        get => _repoUrl;
        set { _repoUrl = value; OnPropertyChanged(); }
    }

    public string Category
    {
        get => string.IsNullOrWhiteSpace(_category) ? "Geral" : _category;
        set
        {
            _category = string.IsNullOrWhiteSpace(value) ? "Geral" : value.Trim();
            OnPropertyChanged();
            OnPropertyChanged(nameof(CategoryDisplay));
            OnPropertyChanged(nameof(CategoryBgColor));
            OnPropertyChanged(nameof(CategoryBorderColor));
            OnPropertyChanged(nameof(CategoryTextColor));
        }
    }

    [JsonIgnore]
    public string CategoryDisplay => $"🏷️ {Category.ToUpperInvariant()}";

    [JsonIgnore]
    public string CategoryBgColor => GetCategoryPalette(Category).Bg;

    [JsonIgnore]
    public string CategoryBorderColor => GetCategoryPalette(Category).Border;

    [JsonIgnore]
    public string CategoryTextColor => GetCategoryPalette(Category).Text;

    private static (string Bg, string Border, string Text) GetCategoryPalette(string category)
    {
        var norm = (category ?? "Geral").Trim().ToLowerInvariant();
        return norm switch
        {
            "ia / agentes" or "ia" or "ai" => ("#3B0764", "#A855F7", "#F3E8FF"),
            "jogos / recomp" or "jogos" or "games" => ("#451A03", "#F59E0B", "#FEF3C7"),
            "engenharia reversa" or "segurança" or "re" => ("#450A0A", "#EF4444", "#FEE2E2"),
            "ferramentas" or "tools" or "utilitários" => ("#064E3B", "#10B981", "#D1FAE5"),
            "web / frontend" or "web" => ("#0C4A6E", "#38BDF8", "#E0F2FE"),
            "backend / api" or "backend" => ("#1E1B4B", "#6366F1", "#E0E7FF"),
            _ => GetDeterministicPalette(norm)
        };
    }

    private static (string Bg, string Border, string Text) GetDeterministicPalette(string norm)
    {
        if (norm == "geral" || string.IsNullOrEmpty(norm))
            return ("#1E293B", "#64748B", "#F1F5F9");

        var palettes = new (string Bg, string Border, string Text)[]
        {
            ("#31102F", "#EC4899", "#FCE7F3"),
            ("#134E4A", "#14B8A6", "#CCFBF1"),
            ("#365314", "#84CC16", "#ECFCCB"),
            ("#1E3A8A", "#3B82F6", "#DBEAFE"),
            ("#4C1D95", "#8B5CF6", "#EDE9FE")
        };
        int hash = Math.Abs(norm.GetHashCode());
        return palettes[hash % palettes.Length];
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnPropertyChanged(); }
    }

    public string DefaultBranch
    {
        get => _defaultBranch;
        set { _defaultBranch = value; OnPropertyChanged(); }
    }

    public string LatestCommitSha
    {
        get => _latestCommitSha;
        set { _latestCommitSha = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShortSha)); }
    }

    [JsonIgnore]
    public string ShortSha => string.IsNullOrWhiteSpace(LatestCommitSha)
        ? "—"
        : LatestCommitSha[..Math.Min(7, LatestCommitSha.Length)];

    public string LatestVersionTag
    {
        get => _latestVersionTag;
        set { _latestVersionTag = value; OnPropertyChanged(); }
    }

    public string LatestUpdateNotes
    {
        get => _latestUpdateNotes;
        set { _latestUpdateNotes = value; OnPropertyChanged(); }
    }

    public string Language
    {
        get => _language;
        set { _language = value; OnPropertyChanged(); }
    }

    public int StarsCount
    {
        get => _starsCount;
        set { _starsCount = value; OnPropertyChanged(); }
    }

    public DateTime LastCheckedAt
    {
        get => _lastCheckedAt;
        set { _lastCheckedAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastCheckedText)); }
    }

    public DateTime LastUpdatedAt
    {
        get => _lastUpdatedAt;
        set { _lastUpdatedAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(LastUpdatedText)); }
    }

    [JsonIgnore]
    public string LastCheckedText => $"Verificado: {LastCheckedAt:dd/MM HH:mm}";

    [JsonIgnore]
    public string LastUpdatedText => $"Último ZIP: {LastUpdatedAt:dd/MM/yyyy HH:mm}";

    [JsonIgnore]
    public bool IsUpdating
    {
        get => _isUpdating;
        set { _isUpdating = value; OnPropertyChanged(); }
    }

    [JsonIgnore]
    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public ObservableCollection<ZipVersionEntry> Versions { get; set; } = new();

    [JsonIgnore]
    public ZipVersionEntry? SelectedVersion
    {
        get => _selectedVersion ?? (Versions.Count > 0 ? Versions[0] : null);
        set
        {
            _selectedVersion = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ActiveNotes));
        }
    }

    [JsonIgnore]
    public string ActiveNotes => SelectedVersion != null && !string.IsNullOrWhiteSpace(SelectedVersion.ReleaseNotes)
        ? SelectedVersion.ReleaseNotes
        : (string.IsNullOrWhiteSpace(LatestUpdateNotes) ? "Sem notas de atualização adicionais." : LatestUpdateNotes);

    public string FolderPath { get; set; } = string.Empty;

    public void NotifyVersionsChanged()
    {
        OnPropertyChanged(nameof(Versions));
        OnPropertyChanged(nameof(SelectedVersion));
        OnPropertyChanged(nameof(ActiveNotes));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public class AppConfig
{
    public string LibraryPath { get; set; } = string.Empty;
    public int CheckIntervalMinutes { get; set; } = 30;
    public bool StartWithWindows { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool AutoUpdateApp { get; set; } = true;
    public string GitHubToken { get; set; } = string.Empty;
    public List<string> Categories { get; set; } = new()
    {
        "Geral",
        "IA / Agentes",
        "Jogos / Recomp",
        "Engenharia Reversa",
        "Ferramentas",
        "Web / Frontend",
        "Backend / API"
    };
    public List<RepositoryItem> Repositories { get; set; } = new();
}
