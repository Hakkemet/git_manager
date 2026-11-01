using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using GitManager.Models;
using GitManager.Services;

namespace GitManager;

public partial class RepositoryDetailsWindow : Window
{
    private readonly RepositoryItem _item;
    private readonly ConfigService _configService;
    private readonly Func<RepositoryItem, Task> _onSyncRequested;
    private readonly Func<RepositoryItem, Task> _onCreateProjectRequested;

    public RepositoryDetailsWindow(
        RepositoryItem item,
        ConfigService configService,
        Func<RepositoryItem, Task> onSyncRequested,
        Func<RepositoryItem, Task> onCreateProjectRequested)
    {
        InitializeComponent();
        _item = item;
        _configService = configService;
        _onSyncRequested = onSyncRequested;
        _onCreateProjectRequested = onCreateProjectRequested;

        DataContext = _item;
        Title = $"{_item.FullName} — Detalhes e Notas de Update";
    }

    private async void SyncRepo_Click(object sender, RoutedEventArgs e)
    {
        SyncModalBtn.IsEnabled = false;
        try
        {
            await _onSyncRequested(_item);
        }
        finally
        {
            SyncModalBtn.IsEnabled = true;
        }
    }

    private async void CreateProject_Click(object sender, RoutedEventArgs e)
    {
        await _onCreateProjectRequested(_item);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = _configService.GetRepositoryFolder(_item);
        Directory.CreateDirectory(folder);
        Process.Start("explorer.exe", folder);
    }

    private void OpenGitHub_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_item.RepoUrl))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _item.RepoUrl,
                UseShellExecute = true
            });
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
