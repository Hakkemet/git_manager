using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GitManager.Models;
using GitManager.Services;
using Forms = System.Windows.Forms;

namespace GitManager;

public partial class MainWindow : Window
{
    private readonly ConfigService _configService;
    private readonly GitHubService _gitHubService;
    private readonly AppSelfUpdateService _selfUpdateService;
    private readonly Action<string, string> _showBalloon;
    private readonly DispatcherTimer _backgroundTimer;

    public ObservableCollection<RepositoryItem> DisplayedRepositories { get; } = new();

    public MainWindow(
        ConfigService configService,
        GitHubService gitHubService,
        AppSelfUpdateService selfUpdateService,
        Action<string, string> showBalloon)
    {
        _configService = configService;
        _gitHubService = gitHubService;
        _selfUpdateService = selfUpdateService;
        _showBalloon = showBalloon;
        _backgroundTimer = new DispatcherTimer();

        InitializeComponent();

        ReposItemsControl.ItemsSource = DisplayedRepositories;

        ApplyLocalizedDynamicLabels();
        RefreshCategorySelectors();
        RefreshRepositoryList();
        UpdateFooterInfo();

        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

        ConfigureBackgroundTimer();

        Loaded += async (_, _) =>
        {
            await RunDailyStartupSyncIfNeededAsync();
        };
    }

    private async Task RunDailyStartupSyncIfNeededAsync()
    {
        var todayKey = DateTime.Now.ToString("yyyy-MM-dd");
        bool alreadySyncedToday = string.Equals(_configService.Config.LastDailySyncDate, todayKey, StringComparison.OrdinalIgnoreCase);

        // Sempre sincroniza na primeira abertura do dia (ou se houver algum repositório ainda sem .zip baixado)
        bool hasPendingRepos = _configService.Config.Repositories.Any(r => r.Versions.Count == 0 || r.StatusMessage.Contains("403") || r.StatusMessage.Contains("Error"));

        if (!alreadySyncedToday || hasPendingRepos)
        {
            _configService.Config.LastDailySyncDate = todayKey;
            _configService.SaveConfig();

            await CheckSelfUpdateSilentAsync();
            if (_configService.Config.Repositories.Count > 0)
            {
                await SyncAllRepositoriesAsync(forceNotification: false);
            }
        }
    }

    private void OnLanguageChanged()
    {
        ApplyLocalizedDynamicLabels();
        RefreshCategorySelectors();
        foreach (var r in _configService.Config.Repositories)
            r.NotifyVersionsChanged();
        RefreshRepositoryList();
        UpdateFooterInfo();
    }

    private void ApplyLocalizedDynamicLabels()
    {
        AppVersionBadge.Text = $"v{AppSelfUpdateService.CurrentVersion} • {LocalizationService.Instance["PortableBadgeSuffix"]}";
        FooterStatusText.Text = LocalizationService.Instance["FooterReadyStatus"];
    }

    private void RefreshCategorySelectors()
    {
        var categories = _configService.Config.Categories;
        foreach (var repo in _configService.Config.Repositories)
        {
            if (!string.IsNullOrWhiteSpace(repo.Category) &&
                !categories.Any(c => string.Equals(c, repo.Category, StringComparison.OrdinalIgnoreCase)))
            {
                categories.Add(repo.Category);
            }
        }

        var defaultCat = LocalizationService.Instance["DefaultCategoryGeneral"];
        var currentNewCat = NewRepoCategoryCombo?.Text;
        if (NewRepoCategoryCombo != null)
        {
            NewRepoCategoryCombo.ItemsSource = null;
            NewRepoCategoryCombo.ItemsSource = categories;
            NewRepoCategoryCombo.Text = string.IsNullOrWhiteSpace(currentNewCat) ? defaultCat : currentNewCat;
        }

        if (CategoryFilterCombo != null)
        {
            int prevIdx = CategoryFilterCombo.SelectedIndex;
            var allLabel = LocalizationService.Instance["AllCategoriesFilter"];
            var filterList = new System.Collections.Generic.List<string> { allLabel };
            filterList.AddRange(categories);
            CategoryFilterCombo.ItemsSource = filterList;

            CategoryFilterCombo.SelectedIndex = (prevIdx >= 0 && prevIdx < filterList.Count) ? prevIdx : 0;
        }
    }

    private void EnsureCategoryExists(string category)
    {
        var clean = string.IsNullOrWhiteSpace(category) ? LocalizationService.Instance["DefaultCategoryGeneral"] : category.Trim();
        if (!_configService.Config.Categories.Any(c => string.Equals(c, clean, StringComparison.OrdinalIgnoreCase)))
        {
            _configService.Config.Categories.Add(clean);
            _configService.SaveConfig();
            RefreshCategorySelectors();
        }
    }

    private void ConfigureBackgroundTimer()
    {
        // A verificação automática ocorre apenas uma vez ao dia no primeiro momento em que o app é aberto.
        _backgroundTimer.Stop();
    }

    private async void BackgroundTimer_Tick(object? sender, EventArgs e)
    {
        await CheckSelfUpdateSilentAsync();
        await SyncAllRepositoriesAsync(forceNotification: false);
    }

    private async Task CheckSelfUpdateSilentAsync()
    {
        if (!_configService.Config.AutoUpdateApp)
            return;

        var res = await _selfUpdateService.CheckForAppUpdateAsync();
        if (res.HasUpdate)
        {
            _showBalloon("GitManager Update", $"Downloading v{res.LatestVersion}...");
            await _selfUpdateService.DownloadAndApplyUpdateAsync(res);
        }
    }

    private void RefreshRepositoryList()
    {
        if (DisplayedRepositories == null || _configService?.Config == null) return;

        var filter = SearchInput?.Text?.Trim() ?? string.Empty;
        var selectedCatFilter = CategoryFilterCombo?.SelectedItem as string;
        var allLabel = LocalizationService.Instance["AllCategoriesFilter"];
        bool filterByCategory = !string.IsNullOrEmpty(selectedCatFilter) &&
                                selectedCatFilter != allLabel &&
                                CategoryFilterCombo?.SelectedIndex > 0;

        var query = _configService.Config.Repositories.AsEnumerable();

        if (filterByCategory)
        {
            query = query.Where(r => string.Equals(r.Category, selectedCatFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(filter))
        {
            query = query.Where(repo =>
                repo.FullName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                repo.Description.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                repo.Category.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                repo.Language.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        int sortIndex = SortFilterCombo?.SelectedIndex ?? 0;
        query = sortIndex switch
        {
            1 => query.OrderByDescending(r => r.StarsCount).ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase),
            2 => query.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Owner, StringComparer.OrdinalIgnoreCase),
            3 => query.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.Owner, StringComparer.OrdinalIgnoreCase),
            4 => query.OrderByDescending(GetRepoActiveZipSizeBytes).ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase),
            5 => query.OrderBy(GetRepoActiveZipSizeBytes).ThenBy(r => r.FullName, StringComparer.OrdinalIgnoreCase),
            _ => query.OrderByDescending(r => r.LastUpdatedAt)
        };

        DisplayedRepositories.Clear();
        foreach (var repo in query)
        {
            DisplayedRepositories.Add(repo);
        }

        if (EmptyStatePanel != null)
        {
            EmptyStatePanel.Visibility = _configService.Config.Repositories.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private static long GetRepoActiveZipSizeBytes(RepositoryItem repo) =>
        repo.SelectedVersion?.FileSizeBytes
        ?? repo.Versions.FirstOrDefault()?.FileSizeBytes
        ?? 0L;

    private void SortFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshRepositoryList();
    }

    private void UpdateFooterInfo()
    {
        if (FooterLibraryPathText != null && _configService?.Config != null)
        {
            FooterLibraryPathText.Text = $"{LocalizationService.Instance["FooterLibraryPathPrefix"]} {_configService.Config.LibraryPath}";
        }
    }

    private async void AddRepo_Click(object sender, RoutedEventArgs e)
    {
        await AddRepositoryFromInputAsync();
    }

    private async void RepoUrlInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await AddRepositoryFromInputAsync();
        }
    }

    private async Task AddRepositoryFromInputAsync()
    {
        var rawUrl = RepoUrlInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(rawUrl))
            return;

        if (!GitHubService.TryParseGitHubUrl(rawUrl, out var owner, out var repoName))
        {
            System.Windows.MessageBox.Show(
                LocalizationService.Instance["MsgInvalidUrlBody"],
                LocalizationService.Instance["MsgInvalidUrlTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var chosenCategory = string.IsNullOrWhiteSpace(NewRepoCategoryCombo.Text)
            ? LocalizationService.Instance["DefaultCategoryGeneral"]
            : NewRepoCategoryCombo.Text.Trim();
        EnsureCategoryExists(chosenCategory);

        var canonicalUrl = $"https://github.com/{owner}/{repoName}";
        var existing = _configService.Config.Repositories.FirstOrDefault(r =>
            string.Equals(r.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Name, repoName, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.Category = chosenCategory;
            _configService.SaveConfig();
            RefreshRepositoryList();
            System.Windows.MessageBox.Show(
                LocalizationService.Instance.Format("MsgAlreadyAddedBody", $"{owner}/{repoName}", chosenCategory),
                LocalizationService.Instance["MsgAlreadyAddedTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            RepoUrlInput.Text = string.Empty;
            await SyncRepositoryItemAsync(existing, forceDownload: false);
            return;
        }

        var newItem = new RepositoryItem
        {
            Owner = owner,
            Name = repoName,
            RepoUrl = canonicalUrl,
            Category = chosenCategory,
            Description = LocalizationService.Instance["StatusCheckingGitHub"],
            StatusMessage = LocalizationService.Instance["StatusAdding"]
        };

        newItem.FolderPath = _configService.GetRepositoryFolder(newItem);
        Directory.CreateDirectory(newItem.FolderPath);

        _configService.Config.Repositories.Insert(0, newItem);
        _configService.SaveConfig();

        RepoUrlInput.Text = string.Empty;
        RefreshRepositoryList();

        await SyncRepositoryItemAsync(newItem, forceDownload: true);
    }

    private void ChangeCategoryBadge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: RepositoryItem item } btn)
            return;

        var menu = new ContextMenu();

        foreach (var cat in _configService.Config.Categories)
        {
            var menuItem = new MenuItem
            {
                Header = $"🏷️ {cat}",
                IsChecked = string.Equals(item.Category, cat, StringComparison.OrdinalIgnoreCase)
            };
            var capturedCat = cat;
            menuItem.Click += (_, _) =>
            {
                item.Category = capturedCat;
                _configService.SaveConfig();
                RefreshRepositoryList();
            };
            menu.Items.Add(menuItem);
        }

        menu.Items.Add(new Separator());

        var newCatItem = new MenuItem
        {
            Header = LocalizationService.Instance["NewCategoryMenuHeader"]
        };
        newCatItem.Click += (_, _) =>
        {
            var customCat = PromptForCategoryName(item.Category);
            if (!string.IsNullOrWhiteSpace(customCat))
            {
                EnsureCategoryExists(customCat);
                item.Category = customCat;
                _configService.SaveConfig();
                RefreshRepositoryList();
            }
        };
        menu.Items.Add(newCatItem);

        menu.PlacementTarget = btn;
        menu.IsOpen = true;
    }

    private string? PromptForCategoryName(string currentCategory)
    {
        var dialog = new Window
        {
            Title = LocalizationService.Instance["NewCategoryModalTitle"],
            Width = 380,
            Height = 195,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#0B0F19")),
            Foreground = System.Windows.Media.Brushes.White
        };

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = LocalizationService.Instance["NewCategoryPromptLabel"],
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10),
            Foreground = System.Windows.Media.Brushes.White
        });

        var textBox = new System.Windows.Controls.TextBox
        {
            Text = currentCategory,
            Style = (Style)System.Windows.Application.Current.FindResource("ModernTextBox"),
            Margin = new Thickness(0, 0, 0, 16)
        };
        textBox.SelectAll();
        root.Children.Add(textBox);

        var btnPanel = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = LocalizationService.Instance["CancelBtn"],
            Style = (Style)System.Windows.Application.Current.FindResource("SecondaryButton"),
            Width = 90,
            Margin = new Thickness(0, 0, 8, 0)
        };
        cancelBtn.Click += (_, _) => { dialog.DialogResult = false; dialog.Close(); };

        var okBtn = new System.Windows.Controls.Button
        {
            Content = LocalizationService.Instance["SaveBtn"],
            Style = (Style)System.Windows.Application.Current.FindResource("PrimaryButton"),
            Width = 100,
            IsDefault = true
        };
        okBtn.Click += (_, _) => { dialog.DialogResult = true; dialog.Close(); };

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(okBtn);
        root.Children.Add(btnPanel);

        dialog.Content = root;
        dialog.Loaded += (_, _) => textBox.Focus();

        return dialog.ShowDialog() == true ? textBox.Text.Trim() : null;
    }

    private void CategoryFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshRepositoryList();
    }

    private async Task SyncRepositoryItemAsync(RepositoryItem item, bool forceDownload)
    {
        FooterStatusText.Text = $"Syncing {item.FullName}...";
        var (updated, msg) = await _gitHubService.SyncRepositoryAsync(item, forceDownload);
        _configService.SaveConfig();
        RefreshRepositoryList();
        FooterStatusText.Text = $"{item.FullName}: {msg}";

        if (updated)
        {
            _showBalloon($"Repository updated (.zip): {item.FullName}", msg);
        }
    }

    public async Task SyncAllRepositoriesAsync(bool forceNotification)
    {
        if (_configService.Config.Repositories.Count == 0)
            return;

        SyncAllBtn.IsEnabled = false;
        int updatedCount = 0;

        try
        {
            foreach (var repo in _configService.Config.Repositories.ToList())
            {
                _configService.ReconcileFolderZipFiles(repo);
                var (updated, _) = await _gitHubService.SyncRepositoryAsync(repo, forceDownload: false);
                if (updated) updatedCount++;
            }

            _configService.SaveConfig();
            RefreshRepositoryList();
            var summary = updatedCount > 0
                ? $"{updatedCount} new .zip version(s) downloaded!"
                : $"{LocalizationService.Instance["StatusUpToDate"]} ({DateTime.Now:HH:mm:ss})";

            FooterStatusText.Text = summary;

            if (updatedCount > 0 || forceNotification)
            {
                _showBalloon("GitManager", summary);
            }
        }
        finally
        {
            SyncAllBtn.IsEnabled = true;
        }
    }

    private async void SyncAll_Click(object sender, RoutedEventArgs e)
    {
        await SyncAllRepositoriesAsync(forceNotification: true);
    }

    private async void SyncSingleRepo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: RepositoryItem item })
        {
            await TriggerSyncForRepoAsync(item);
        }
    }

    private async Task TriggerSyncForRepoAsync(RepositoryItem item)
    {
        _configService.ReconcileFolderZipFiles(item);
        bool force = item.Versions.Count == 0;
        await SyncRepositoryItemAsync(item, forceDownload: force);
    }

    private void OpenRepoDetails_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: RepositoryItem item })
            return;

        var modal = new RepositoryDetailsWindow(
            item,
            _configService,
            TriggerSyncForRepoAsync,
            PromptAndCreateProjectAsync)
        {
            Owner = this
        };
        modal.ShowDialog();
    }

    private async void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: RepositoryItem item })
            return;

        await PromptAndCreateProjectAsync(item);
    }

    private async Task PromptAndCreateProjectAsync(RepositoryItem item)
    {
        var selectedZip = item.SelectedVersion ?? item.Versions.FirstOrDefault();
        if (selectedZip == null || !File.Exists(selectedZip.FullPath))
        {
            System.Windows.MessageBox.Show(
                LocalizationService.Instance["MsgNoZipFoundBody"],
                LocalizationService.Instance["MsgNoZipFoundTitle"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        using var folderDialog = new Forms.FolderBrowserDialog
        {
            Description = LocalizationService.Instance.Format("MsgSelectProjectFolderDesc", item.Name, selectedZip.DisplayTitle),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true
        };

        if (folderDialog.ShowDialog() != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(folderDialog.SelectedPath))
            return;

        var targetFolder = folderDialog.SelectedPath;

        if (Directory.Exists(targetFolder) && Directory.EnumerateFileSystemEntries(targetFolder).Any())
        {
            var subPath = Path.Combine(targetFolder, item.Name);
            var choice = System.Windows.MessageBox.Show(
                LocalizationService.Instance.Format("MsgFolderNotEmptyBody", item.Name, subPath, targetFolder),
                LocalizationService.Instance["MsgFolderNotEmptyTitle"],
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (choice == MessageBoxResult.Cancel)
                return;
            if (choice == MessageBoxResult.Yes)
                targetFolder = subPath;
        }

        try
        {
            item.StatusMessage = LocalizationService.Instance["StatusExtracting"];
            await _gitHubService.CreateProjectFromZipAsync(selectedZip, targetFolder);
            item.StatusMessage = LocalizationService.Instance["StatusProjectCreated"];

            var openExplorer = System.Windows.MessageBox.Show(
                LocalizationService.Instance.Format("MsgProjectCreatedBody", selectedZip.FileName, targetFolder),
                LocalizationService.Instance["MsgProjectCreatedTitle"],
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (openExplorer == MessageBoxResult.Yes)
            {
                Process.Start("explorer.exe", targetFolder);
            }
        }
        catch (Exception ex)
        {
            item.StatusMessage = LocalizationService.Instance["StatusExtractError"];
            System.Windows.MessageBox.Show(
                ex.Message,
                "Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenRepoFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: RepositoryItem item })
        {
            var folder = _configService.GetRepositoryFolder(item);
            Directory.CreateDirectory(folder);
            Process.Start("explorer.exe", folder);
        }
    }

    private void OpenGitHubUrl_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: RepositoryItem item } && !string.IsNullOrWhiteSpace(item.RepoUrl))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.RepoUrl,
                UseShellExecute = true
            });
        }
    }

    private void RemoveRepo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: RepositoryItem item })
            return;

        var res = System.Windows.MessageBox.Show(
            LocalizationService.Instance.Format("MsgRemoveRepoBody", item.FullName),
            LocalizationService.Instance["MsgRemoveRepoTitle"],
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            _configService.Config.Repositories.Remove(item);
            _configService.SaveConfig();
            RefreshRepositoryList();
        }
    }

    private void OpenLibraryRoot_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_configService.Config.LibraryPath);
        Process.Start("explorer.exe", _configService.Config.LibraryPath);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow(_configService, _selfUpdateService)
        {
            Owner = this
        };
        if (win.ShowDialog() == true && win.SettingsSaved)
        {
            ConfigureBackgroundTimer();
            UpdateFooterInfo();
            RefreshRepositoryList();
        }
    }

    private void RepoUrlInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (RepoUrlPlaceholder != null)
        {
            RepoUrlPlaceholder.Visibility = string.IsNullOrEmpty(RepoUrlInput.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder != null)
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchInput.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        RefreshRepositoryList();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        if (_configService?.Config?.MinimizeToTrayOnClose == true)
        {
            e.Cancel = true;
            Hide();
            _showBalloon(
                LocalizationService.Instance["TrayMinimizedTitle"],
                LocalizationService.Instance["TrayMinimizedMsg"]);
        }
        else
        {
            if (System.Windows.Application.Current is App app)
                app.ExitApplication();
        }
    }
}
