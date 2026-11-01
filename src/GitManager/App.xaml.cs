using System;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GitManager.Services;
using Forms = System.Windows.Forms;

namespace GitManager;

public partial class App : System.Windows.Application
{
    private const string PipeName = "GitManager_IPC_Pipe_v1";
    private Mutex? _singleInstanceMutex;
    private CancellationTokenSource? _pipeCts;
    private Forms.NotifyIcon? _notifyIcon;
    private MainWindow? _mainWindow;

    public ConfigService ConfigService { get; private set; } = null!;
    public GitHubService GitHubService { get; private set; } = null!;
    public AppSelfUpdateService AppSelfUpdateService { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            LogError("DispatcherUnhandledException", args.Exception);
            System.Windows.MessageBox.Show(
                $"Erro inesperado na interface:\n{args.Exception.Message}\n\nDetalhes salvos em startup_error.log.",
                "GitManager — Erro",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                LogError("DomainUnhandledException", ex);
        };

        try
        {
            _singleInstanceMutex = new Mutex(false, "GitManager_SingleInstance_Mutex_v4", out bool isNewInstance);
            if (!isNewInstance)
            {
                SignalRunningInstanceToShow();
                Shutdown();
                return;
            }

            StartSingleInstancePipeServer();

            ConfigService = new ConfigService();
            GitHubService = new GitHubService(ConfigService);
            AppSelfUpdateService = new AppSelfUpdateService(ConfigService);

            SetupTrayIcon();

            _mainWindow = new MainWindow(ConfigService, GitHubService, AppSelfUpdateService, ShowTrayBalloon);
            MainWindow = _mainWindow;

            bool startInTray = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            if (startInTray)
            {
                _mainWindow.Hide();
                ShowTrayBalloon("GitManager ativo em segundo plano", "Monitorando e mantendo seus repositórios .zip atualizados.");
            }
            else
            {
                _mainWindow.Show();
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Activate();
                _mainWindow.Topmost = true;
                _mainWindow.Topmost = false;
                _mainWindow.Focus();
            }
        }
        catch (Exception ex)
        {
            LogError("StartupException", ex);
            System.Windows.MessageBox.Show(
                $"Falha ao iniciar o GitManager:\n{ex.Message}\n\nVeja startup_error.log para mais detalhes.",
                "GitManager — Falha de Inicialização",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void StartSingleInstancePipeServer()
    {
        _pipeCts = new CancellationTokenSource();
        var token = _pipeCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(token);
                    using var reader = new StreamReader(server);
                    var cmd = await reader.ReadLineAsync(token);
                    if (string.Equals(cmd, "SHOW", StringComparison.OrdinalIgnoreCase))
                    {
                        Dispatcher.Invoke(RestoreMainWindow);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(500, token);
                }
            }
        }, token);
    }

    private static void SignalRunningInstanceToShow()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine("SHOW");
        }
        catch
        {
            // Se não responder no pipe, avisa o usuário
            System.Windows.MessageBox.Show(
                "O GitManager já está aberto na bandeja do sistema (perto do relógio).",
                "GitManager",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void SetupTrayIcon()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "GitManager - Biblioteca de Repositórios (.zip)",
            Icon = CreateTrayIcon(),
            Visible = true
        };

        var contextMenu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Abrir GitManager");
        openItem.Font = new Font(openItem.Font, System.Drawing.FontStyle.Bold);
        openItem.Click += (_, _) => RestoreMainWindow();

        var syncNowItem = new Forms.ToolStripMenuItem("Sincronizar Todos os Repositórios Agora");
        syncNowItem.Click += async (_, _) =>
        {
            if (_mainWindow != null)
                await _mainWindow.SyncAllRepositoriesAsync(forceNotification: true);
        };

        var openLibraryItem = new Forms.ToolStripMenuItem("Abrir Pasta GitManagerLibrary");
        openLibraryItem.Click += (_, _) =>
        {
            if (Directory.Exists(ConfigService.Config.LibraryPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", ConfigService.Config.LibraryPath);
            }
        };

        var exitItem = new Forms.ToolStripMenuItem("Sair Completamente");
        exitItem.Click += (_, _) => ExitApplication();

        contextMenu.Items.Add(openItem);
        contextMenu.Items.Add(syncNowItem);
        contextMenu.Items.Add(openLibraryItem);
        contextMenu.Items.Add(new Forms.ToolStripSeparator());
        contextMenu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.DoubleClick += (_, _) => RestoreMainWindow();
        _notifyIcon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left)
                RestoreMainWindow();
        };
    }

    private static Icon CreateTrayIcon()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                var extracted = Icon.ExtractAssociatedIcon(exePath);
                if (extracted != null)
                    return extracted;
            }
        }
        catch
        {
            // Fallback abaixo
        }
        return SystemIcons.Application;
    }

    public void ShowTrayBalloon(string title, string message)
    {
        try
        {
            _notifyIcon?.ShowBalloonTip(3500, title, message, Forms.ToolTipIcon.Info);
        }
        catch
        {
            // Ignora falhas de notificação do Windows
        }
    }

    public void RestoreMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    public void ExitApplication()
    {
        try
        {
            _pipeCts?.Cancel();
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
            _singleInstanceMutex?.Dispose();
        }
        catch
        {
            // Ignora erros no encerramento
        }
        Shutdown();
    }

    private static void LogError(string context, Exception ex)
    {
        try
        {
            var logPath = Path.Combine(AppContext.BaseDirectory, "startup_error.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{context}]\n{ex}\n\n");
        }
        catch
        {
            // Ignora erro de gravação de log
        }
    }
}
