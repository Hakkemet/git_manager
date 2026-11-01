using System;
using System.Windows;
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
        _configService = configService;
        _selfUpdateService = selfUpdateService;

        var cfg = _configService.Config;
        LibraryPathBox.Text = cfg.LibraryPath;
        IntervalBox.Text = cfg.CheckIntervalMinutes.ToString();
        StartWithWindowsCheck.IsChecked = cfg.StartWithWindows;
        MinimizeToTrayCheck.IsChecked = cfg.MinimizeToTrayOnClose;
        GitHubTokenBox.Text = cfg.GitHubToken;
    }

    private void BrowseLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Escolha a pasta onde a biblioteca GitManagerLibrary ficará armazenada",
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
        UpdateStatusText.Text = $"Verificando atualizações (versão atual: v{AppSelfUpdateService.CurrentVersion})...";

        try
        {
            var res = await _selfUpdateService.CheckForAppUpdateAsync();
            if (res.HasUpdate)
            {
                var confirm = System.Windows.MessageBox.Show(
                    $"Nova versão do GitManager encontrada: v{res.LatestVersion}!\n\nNotas:\n{res.ReleaseNotes}\n\nDeseja baixar e atualizar o aplicativo agora?",
                    "Atualização Disponível",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm == MessageBoxResult.Yes)
                {
                    UpdateStatusText.Text = "Baixando e aplicando atualização...";
                    var applyRes = await _selfUpdateService.DownloadAndApplyUpdateAsync(res);
                    UpdateStatusText.Text = applyRes.Message;
                }
                else
                {
                    UpdateStatusText.Text = $"Nova versão v{res.LatestVersion} disponível.";
                }
            }
            else
            {
                UpdateStatusText.Text = $"Você já está na versão mais recente (v{res.CurrentVersion}). {res.ReleaseNotes}";
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

        if (int.TryParse(IntervalBox.Text.Trim(), out int minutes) && minutes >= 1)
            cfg.CheckIntervalMinutes = minutes;
        else
            cfg.CheckIntervalMinutes = 30;

        cfg.StartWithWindows = StartWithWindowsCheck.IsChecked == true;
        cfg.MinimizeToTrayOnClose = MinimizeToTrayCheck.IsChecked == true;
        cfg.GitHubToken = GitHubTokenBox.Text.Trim();

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
