using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GitManager.Services;
using Forms = System.Windows.Forms;

namespace GitManager;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService;
    private readonly AppSelfUpdateService _selfUpdateService;

    public bool SettingsSaved { get; private set; }

    public SettingsWindow(ConfigService configService, AppSelfUpdateService selfUpdateService)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _configService = configService;
        _selfUpdateService = selfUpdateService;

        var cfg = _configService.Config;
        LibraryPathBox.Text = cfg.LibraryPath;
        InstallPathBox.Text = InstallFolder;
        StartWithWindowsCheck.IsChecked = cfg.StartWithWindows;
        MinimizeToTrayCheck.IsChecked = cfg.MinimizeToTrayOnClose;
        GitHubTokenBox.Text = cfg.GitHubToken;

        var currentLang = string.IsNullOrWhiteSpace(cfg.Language) ? LocalizationService.DefaultLanguage : cfg.Language;
        LanguageComboBox.SelectedIndex = currentLang.Equals("pt_BR", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private static string InstallFolder =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private void OpenInstallFolder_Click(object sender, RoutedEventArgs e)
    {
        Process.Start("explorer.exe", InstallFolder);
    }

    private void BrowseLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = LocalizationService.Instance["SettingsLibraryPathLabel"],
            SelectedPath = LibraryPathBox.Text,
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            LibraryPathBox.Text = dialog.SelectedPath;
        }
    }

    private async void CheckAppUpdate_Click(object sender, RoutedEventArgs e)
    {
        _configService.Config.GitHubToken = GitHubTokenBox.Text.Trim();
        _configService.SaveConfig();

        CheckAppUpdateBtn.IsEnabled = false;
        UpdateStatusText.Text = $"Checking for updates (v{AppSelfUpdateService.CurrentVersion})...";

        try
        {
            var res = await _selfUpdateService.CheckForAppUpdateAsync();
            if (res.HasUpdate)
            {
                var confirm = System.Windows.MessageBox.Show(
                    $"New GitManager version found: v{res.LatestVersion}!\n\n{res.ReleaseNotes}\n\nUpdate now?",
                    "Update Available",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm == MessageBoxResult.Yes)
                {
                    var applyRes = await _selfUpdateService.DownloadAndApplyUpdateAsync(res);
                    UpdateStatusText.Text = applyRes.Message;
                }
                else
                {
                    UpdateStatusText.Text = $"v{res.LatestVersion} available.";
                }
            }
            else
            {
                UpdateStatusText.Text = $"Up to date (v{res.CurrentVersion}).";
            }
        }
        finally
        {
            CheckAppUpdateBtn.IsEnabled = true;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _configService.Config;
        if (!string.IsNullOrWhiteSpace(LibraryPathBox.Text))
            cfg.LibraryPath = LibraryPathBox.Text.Trim();

        cfg.CheckIntervalMinutes = 1440;

        if (LanguageComboBox.SelectedItem is ComboBoxItem { Tag: string selectedLang })
            cfg.Language = selectedLang;
        else
            cfg.Language = LocalizationService.DefaultLanguage;

        cfg.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        cfg.MinimizeToTrayOnClose = MinimizeToTrayCheck.IsChecked == true;
        cfg.GitHubToken = GitHubTokenBox.Text.Trim();

        LocalizationService.Instance.LoadLanguage(cfg.Language, _configService.BaseDirectory);
        _configService.EnsureLibraryDirectories();
        _configService.SyncWindowsStartup(cfg.StartWithWindows);
        _configService.SaveConfig();

        SettingsSaved = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
