using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Drageon.Core;
using Drageon.Infrastructure;

namespace Drageon.App;

public partial class App : System.Windows.Application
{
    private const string ShowCommand = "show";
    private const string StartAllCommand = "start-all";
    private const string StopAllCommand = "stop-all";
    private const string RestartAllCommand = "restart-all";
    private const string ExitCommand = "exit";
    private const string PowerShellCommand = "powershell";
    private AppServices? _services;
    private ProcessManager? _processManager;
    private PortablePathService? _paths;
    private ISettingsService? _settings;
    private IRuntimeController? _runtimeController;
    private IAppLogService? _logs;
    private ICronScheduler? _cronScheduler;
    private ISmtpCatcher? _smtpCatcher;
    private NativeTrayIcon? _trayIcon;
    private SingleInstanceCoordinator? _singleInstance;
    private CancellationTokenSource? _commandListenerLifetime;
    private bool _trayRuntimeActionRunning;
    private bool _isExiting;
    private bool _processManagerDisposed;
    private bool _backgroundServicesDisposed;
    private IReadOnlyList<(string Id, string Title)> _navigation = [];

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogException(args.ExceptionObject as Exception ?? new Exception(args.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogException(args.Exception);
            args.SetObserved();
        };
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Drageon's UI is mostly static. Avoid WPF's sizeable per-process D3D allocation.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        _ = SetCurrentProcessExplicitAppUserModelID("Drageon");
        base.OnStartup(e);
        try
        {
            _paths = new PortablePathService();
            var startupCommand = ParseStartupCommand(e.Args);
            _singleInstance = new SingleInstanceCoordinator(_paths.AppRoot);
            if (!_singleInstance.IsPrimaryInstance)
            {
                await _singleInstance.SendAsync(startupCommand ?? ShowCommand);
                Shutdown();
                return;
            }

            _paths.EnsureDirectoryLayout();
            var store = new JsonFileStore();
            var settings = new SettingsService(_paths, store);
            await settings.LoadAsync();
            _settings = settings;
            var language = new LanguageService(_paths);
            await language.LoadAsync(settings.Current.Language);
            Localization.Current = language;

            var logs = new AppLogService(_paths);
            _logs = logs;
            logs.Write("Application", Localization.Get("app.log.started"));
            _processManager = new ProcessManager(logs, language);
            var generators = new IConfigGenerator[]
            {
                new ApacheConfigGenerator(_paths, store),
                new NginxConfigGenerator(_paths, store),
                new PhpConfigGenerator(_paths, store),
                new MariaDbConfigGenerator(_paths, store),
                new MySqlConfigGenerator(_paths, store),
                new PhpMyAdminConfigGenerator(_paths, store)
            };
            var generatorsById = generators.ToDictionary(generator => generator.ComponentId);
            var webServers = new WebServerRegistry(
            [
                new ApacheWebServer(_paths, store, _processManager, generatorsById, logs, language),
                new NginxWebServer(_paths, store, _processManager, generatorsById, logs, language)
            ]);
            var runtime = new RuntimeController(_paths, store, _processManager, generators, webServers, logs, language);
            _runtimeController = runtime;
            var packages = new PackageManager(_paths, store, language);
            await packages.RefreshEnvironmentAsync();
            var projects = new ProjectDiscoveryService(_paths, store, settings);
            var databaseBackups = new DatabaseBackupService(_paths, store, logs);
            var projectSnapshots = new ProjectSnapshotService(_paths, store, projects, databaseBackups, logs);
            var staging = new StagingService(_paths, store, projects, databaseBackups, projectSnapshots, logs);
            _cronScheduler = new CronScheduler(_paths, store, logs, language);
            _cronScheduler.JobFinished += OnCronJobFinished;
            runtime.WebServerStarted += OnWebServerStarted;
            _smtpCatcher = new SmtpCatcher(_paths, store, settings, logs, language);
            _smtpCatcher.MessageReceived += OnMailReceived;
            var services = new AppServices(_paths, store, settings, language, packages, projects, databaseBackups, projectSnapshots, staging, _processManager, runtime, webServers, generators, logs, _cronScheduler, _smtpCatcher);
            _services = services;

            var window = CreateMainWindow();
            MainWindow = window;
            InitializeTray(window);
            InitializeTaskbarJumpList();
            StartCommandListener();
            window.Show();

            await _cronScheduler.StartAsync();
            try
            {
                await _smtpCatcher.StartAsync();
            }
            catch (Exception exception)
            {
                logs.Write("Mail", string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("app.log.smtpFailed"), exception.Message), LogSeverity.Error);
            }

            if (settings.Current.StartComponentsWithApp)
            {
                try
                {
                    await runtime.StartAllAsync();
                }
                catch (Exception exception)
                {
                    logs.Write("Application", string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("app.log.autoStartFailed"), exception.Message), LogSeverity.Error);
                }
            }

            if (startupCommand is not null) await ExecuteApplicationCommandAsync(startupCommand);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(exception.Message, Localization.Get("app.startFailed"), MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _commandListenerLifetime?.Cancel();
        _commandListenerLifetime?.Dispose();
        _trayIcon?.Dispose();
        if (!_backgroundServicesDisposed)
        {
            if (_runtimeController is not null) _runtimeController.WebServerStarted -= OnWebServerStarted;
            if (_smtpCatcher is not null) _smtpCatcher.MessageReceived -= OnMailReceived;
            if (_cronScheduler is not null) _cronScheduler.JobFinished -= OnCronJobFinished;
            _smtpCatcher?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _cronScheduler?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _backgroundServicesDisposed = true;
        }
        if (_processManager is not null && !_processManagerDisposed)
        {
            _runtimeController?.StopAllAsync().GetAwaiter().GetResult();
            _processManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _processManagerDisposed = true;
        }

        _singleInstance?.Dispose();

        base.OnExit(e);
    }

    private void InitializeTray(MainWindow window)
    {
        if (window.DataContext is ViewModels.MainViewModel viewModel)
            _navigation = viewModel.Navigation.Select(item => (item.Id, item.Title)).ToArray();
        _trayIcon = new NativeTrayIcon("Drageon")
        {
            DoubleClickAction = ShowMainWindow,
            MenuProvider = BuildTrayMenuAsync
        };
    }

    private async Task<IReadOnlyList<NativeTrayMenuItem>> BuildTrayMenuAsync()
    {
        var navigation = _navigation.Select(item => new NativeTrayMenuItem(item.Title, () =>
        {
            ShowPage(item.Id);
            return Task.CompletedTask;
        })).ToArray();

        var quickAccess = new List<NativeTrayMenuItem>
        {
            new("localhost", () => OpenQuickAccessAsync("localhost"))
        };
        try
        {
            if (await GetPhpMyAdminUrlAsync() is not null)
                quickAccess.Add(new("phpMyAdmin", () => OpenQuickAccessAsync("phpmyadmin")));
        }
        catch (Exception exception)
        {
            _logs?.Write("Application", exception.Message, LogSeverity.Error);
        }
        quickAccess.Add(new("PowerShell", () => OpenQuickAccessAsync(PowerShellCommand)));

        return
        [
            new(Localization.Get("tray.open"), () =>
            {
                ShowMainWindow();
                return Task.CompletedTask;
            }),
            new(Localization.Get("tray.navigation"), Children: navigation),
            new(Localization.Get("tray.quickAccess"), Children: quickAccess),
            NativeTrayMenuItem.Separator,
            TrayRuntimeItem("tray.server.start", () => _runtimeController!.StartAllAsync(), "tray.server.started"),
            TrayRuntimeItem("tray.server.stop", () => _runtimeController!.StopAllAsync(), "tray.server.stopped"),
            TrayRuntimeItem("tray.server.restart", RestartServersAsync, "tray.server.restarted"),
            NativeTrayMenuItem.Separator,
            new(Localization.Get("tray.exit"), ExitApplicationAsync)
        ];
    }

    private void InitializeTaskbarJumpList()
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) return;

        try
        {
            var jumpList = new JumpList
            {
                ShowFrequentCategory = false,
                ShowRecentCategory = false
            };
            AddJumpTask(jumpList, Localization.Get("tray.server.start"), StartAllCommand, executable);
            AddJumpTask(jumpList, Localization.Get("tray.server.stop"), StopAllCommand, executable);
            AddJumpTask(jumpList, Localization.Get("tray.server.restart"), RestartAllCommand, executable);
            AddJumpTask(jumpList, Localization.Get("tray.openPowerShell"), PowerShellCommand, executable);
            AddJumpTask(jumpList, Localization.Get("tray.exit"), ExitCommand, executable);
            JumpList.SetJumpList(this, jumpList);
            jumpList.Apply();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            _logs?.Write("Application", exception.Message, LogSeverity.Warning);
        }
    }

    private void AddJumpTask(JumpList jumpList, string title, string command, string executable)
    {
        jumpList.JumpItems.Add(new JumpTask
        {
            Title = title,
            Description = title,
            ApplicationPath = executable,
            Arguments = $"--drageon-command={command}",
            WorkingDirectory = _paths?.AppRoot ?? AppContext.BaseDirectory,
            IconResourcePath = executable,
            IconResourceIndex = 0
        });
    }

    private void StartCommandListener()
    {
        if (_singleInstance is null) return;
        _commandListenerLifetime = new CancellationTokenSource();
        _ = ListenForCommandsAsync(_commandListenerLifetime.Token);
    }

    private async Task ListenForCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _singleInstance!.ListenAsync(
                command => Dispatcher.InvokeAsync(() => ExecuteApplicationCommandAsync(command)).Task.Unwrap(),
                cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logs?.Write("Application", exception.Message, LogSeverity.Error);
        }
    }

    private async Task ExecuteApplicationCommandAsync(string command)
    {
        switch (command)
        {
            case PowerShellCommand:
                await OpenQuickAccessAsync(PowerShellCommand);
                break;
            case StartAllCommand when _runtimeController is not null:
                await ExecuteTrayRuntimeActionAsync(() => _runtimeController.StartAllAsync(), "tray.server.started");
                break;
            case StopAllCommand when _runtimeController is not null:
                await ExecuteTrayRuntimeActionAsync(() => _runtimeController.StopAllAsync(), "tray.server.stopped");
                break;
            case RestartAllCommand:
                await ExecuteTrayRuntimeActionAsync(RestartServersAsync, "tray.server.restarted");
                break;
            case ExitCommand:
                await ExitApplicationAsync();
                break;
            default:
                ShowMainWindow();
                break;
        }
    }

    private static string? ParseStartupCommand(IEnumerable<string> arguments)
    {
        const string prefix = "--drageon-command=";
        var value = arguments.FirstOrDefault(argument => argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (value is null) return null;
        var command = value[prefix.Length..].ToLowerInvariant();
        return command is StartAllCommand or StopAllCommand or RestartAllCommand or ExitCommand or ShowCommand or PowerShellCommand
            ? command
            : ShowCommand;
    }

    private async Task OpenQuickAccessAsync(string target)
    {
        if (_services is null || _isExiting) return;
        try
        {
            if (target == PowerShellCommand)
            {
                _logs?.Write("Application", await PowerShellLauncher.OpenAsync(_services));
                return;
            }

            var url = target == "phpmyadmin" ? await GetPhpMyAdminUrlAsync() : GetLocalUrl();
            if (url is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            _logs?.Write("Application", exception.Message, LogSeverity.Error);
            _trayIcon?.ShowBalloon(3000, "Drageon", exception.Message, NativeTrayBalloonIcon.Error);
        }
    }

    private string GetLocalUrl(string relativePath = "")
    {
        var port = _settings?.Current.HttpPort ?? 80;
        return $"http://localhost{(port == 80 ? "" : $":{port}")}/{relativePath.TrimStart('/')}";
    }

    private async Task<string?> GetPhpMyAdminUrlAsync()
    {
        if (_services is null) return null;
        var installed = await _services.Packages.GetInstalledAsync();
        var phpMyAdmin = installed.FirstOrDefault(item => item.Id.Equals("phpmyadmin", StringComparison.OrdinalIgnoreCase) && item.IsActive);
        if (phpMyAdmin is null) return null;
        var manifests = await _services.Packages.GetManifestsAsync();
        var manifest = manifests.FirstOrDefault(item => item.Id.Equals("phpmyadmin", StringComparison.OrdinalIgnoreCase));
        var launchPath = manifest?.Releases.FirstOrDefault(release => release.Version == phpMyAdmin.Version)?.LaunchPath ?? "phpmyadmin/";
        return GetLocalUrl(launchPath);
    }

    private void ShowMainWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (MainWindow is MainWindow existing)
            {
                existing.RestoreFromTray();
                return;
            }
            if (_services is null || _isExiting) return;
            var window = CreateMainWindow();
            MainWindow = window;
            window.Show();
        });
    }

    private void ShowPage(string id)
    {
        Dispatcher.Invoke(() =>
        {
            ShowMainWindow();
            if (MainWindow is not MainWindow window) return;
            if (window.DataContext is ViewModels.MainViewModel viewModel) viewModel.NavigateTo(id);
        });
    }

    private MainWindow CreateMainWindow()
    {
        var window = new MainWindow
        {
            DataContext = new ViewModels.MainViewModel(_services!)
        };
        window.HiddenToTray += OnWindowHiddenToTray;
        window.Closed += OnMainWindowClosed;
        return window;
    }

    private void OnWindowHiddenToTray(object? sender, EventArgs e) =>
        _trayIcon?.ShowBalloon(2500, Localization.Get("tray.hidden.title"),
            Localization.Get("tray.hidden.text"), NativeTrayBalloonIcon.Info);

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window) return;
        window.HiddenToTray -= OnWindowHiddenToTray;
        window.Closed -= OnMainWindowClosed;
        if (window.DataContext is IDisposable disposable) disposable.Dispose();
        window.DataContext = null;
        if (ReferenceEquals(MainWindow, window)) MainWindow = null;
        if (!_isExiting)
        {
            Dispatcher.BeginInvoke(() =>
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
            }, DispatcherPriority.ApplicationIdle);
        }
    }

    private NativeTrayMenuItem TrayRuntimeItem(string titleKey, Func<Task> action, string successKey) =>
        new(Localization.Get(titleKey), () => ExecuteTrayRuntimeActionAsync(action, successKey), IsEnabled: !_trayRuntimeActionRunning);

    private async Task RestartServersAsync()
    {
        if (_runtimeController is null) return;
        await _runtimeController.StopAllAsync();
        await _runtimeController.StartAllAsync();
    }

    private async Task ExecuteTrayRuntimeActionAsync(Func<Task> action, string successKey)
    {
        if (_trayRuntimeActionRunning)
        {
            _trayIcon?.ShowBalloon(2000, "Drageon", Localization.Get("tray.server.busy"), NativeTrayBalloonIcon.Info);
            return;
        }

        _trayRuntimeActionRunning = true;
        try
        {
            await action();
            var message = Localization.Get(successKey);
            _logs?.Write("Application", message);
            _trayIcon?.ShowBalloon(2000, "Drageon", message, NativeTrayBalloonIcon.Info);
        }
        catch (Exception exception)
        {
            var message = string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("tray.server.failed"), exception.Message);
            _logs?.Write("Application", message, LogSeverity.Error);
            _trayIcon?.ShowBalloon(3000, "Drageon", message, NativeTrayBalloonIcon.Error);
        }
        finally
        {
            _trayRuntimeActionRunning = false;
        }
    }

    private void OnMailReceived(object? sender, CapturedMailMessage message)
    {
        if (_isExiting) return;
        Dispatcher.BeginInvoke(() =>
        {
            var settings = _settings?.Current;
            if (settings is null) return;

            if (settings.NotifyOnMailReceived && _trayIcon is not null)
            {
                var subject = string.IsNullOrWhiteSpace(message.Subject)
                    ? Localization.Get("mail.subject.empty")
                    : SingleLine(message.Subject, 110);
                var senderText = string.IsNullOrWhiteSpace(message.From)
                    ? Localization.Get("mail.sender.unknown")
                    : SingleLine(message.From, 110);
                var notification = string.Format(
                    System.Globalization.CultureInfo.CurrentCulture,
                    Localization.Get("mail.notification.message"),
                    subject,
                    senderText);
                _trayIcon.ShowBalloon(
                    4000,
                    Localization.Get("mail.notification.title"),
                    notification,
                    NativeTrayBalloonIcon.Info);
            }

            if (settings.PlaySoundOnMailReceived)
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
        });
    }

    private void OnCronJobFinished(object? sender, CronJobRunResult result)
    {
        if (_isExiting) return;
        Dispatcher.BeginInvoke(() =>
        {
            var settings = _settings?.Current;
            if (settings is null) return;
            if (settings.NotifyOnCronJobFinished && _trayIcon is not null)
            {
                var message = string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Localization.Get(result.Success ? "cron.notification.success" : "cron.notification.failed"),
                    SingleLine(result.JobName, 80), SingleLine(result.Message, 150));
                _trayIcon.ShowBalloon(4000, Localization.Get("cron.notification.title"), message,
                    result.Success ? NativeTrayBalloonIcon.Info : NativeTrayBalloonIcon.Error);
            }
            if (settings.PlaySoundOnCronJobFinished)
            {
                if (result.Success) System.Media.SystemSounds.Asterisk.Play();
                else System.Media.SystemSounds.Hand.Play();
            }
        });
    }

    private async void OnWebServerStarted(object? sender, EventArgs e)
    {
        if (_isExiting || _cronScheduler is null) return;
        try
        {
            await _cronScheduler.RunMissedJobsAsync();
        }
        catch (Exception exception)
        {
            _logs?.Write("Cron", exception.Message, LogSeverity.Error);
        }
    }

    private static string SingleLine(string value, int maximumLength)
    {
        var result = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return result.Length <= maximumLength ? result : result[..(maximumLength - 1)] + "…";
    }

    private async Task ExitApplicationAsync()
    {
        if (_isExiting) return;
        _isExiting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;

        if (_processManager is not null && !_processManagerDisposed)
        {
            if (_runtimeController is not null) await _runtimeController.StopAllAsync();
            await _processManager.DisposeAsync();
            _processManagerDisposed = true;
        }

        if (!_backgroundServicesDisposed)
        {
            if (_runtimeController is not null) _runtimeController.WebServerStarted -= OnWebServerStarted;
            if (_smtpCatcher is not null) _smtpCatcher.MessageReceived -= OnMailReceived;
            if (_smtpCatcher is not null) await _smtpCatcher.DisposeAsync();
            if (_cronScheduler is not null) await _cronScheduler.DisposeAsync();
            _backgroundServicesDisposed = true;
        }

        if (MainWindow is MainWindow window)
        {
            window.AllowClose = true;
            window.Close();
        }

        Shutdown();
    }

    internal Task RequestExitAsync() => ExitApplicationAsync();

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogException(e.Exception);
        System.Windows.MessageBox.Show(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("app.unexpectedError"), e.Exception.Message),
            Localization.Get("app.errorTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private void LogException(Exception exception)
    {
        try
        {
            var logPath = _paths?.Resolve("app", "var", "logs", "app", "fatal.log")
                ?? Path.Combine(AppContext.BaseDirectory, "app", "var", "logs", "app", "fatal.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, $"[{DateTimeOffset.Now:O}]\n{exception}\n\n");
        }
        catch (IOException)
        {
            // Fehlerprotokollierung darf keinen zweiten Absturz verursachen.
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}

public sealed record AppServices(
    IPortablePathService Paths,
    JsonFileStore Store,
    ISettingsService Settings,
    ILanguageService Language,
    IPackageManager Packages,
    IProjectDiscoveryService Projects,
    IDatabaseBackupService DatabaseBackups,
    IProjectSnapshotService ProjectSnapshots,
    IStagingService Staging,
    IProcessManager Processes,
    IRuntimeController Runtime,
    IWebServerRegistry WebServers,
    IReadOnlyList<IConfigGenerator> Generators,
    IAppLogService Logs,
    ICronScheduler Cron,
    ISmtpCatcher Mail);
