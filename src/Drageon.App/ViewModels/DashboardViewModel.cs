using System.Collections.ObjectModel;
using System.Windows;
using Drageon.Core;

namespace Drageon.App.ViewModels;

public sealed class DashboardViewModel : ViewModelBase, IRefreshable
{
    private readonly AppServices _services;
    private readonly ComponentCardViewModel _databaseComponent;
    private string _message;
    private bool _isPhpMyAdminInstalled;
    private string _phpMyAdminPath = "phpmyadmin/";
    private ServerOperation _serverOperation;

    public DashboardViewModel(AppServices services)
    {
        _services = services;
        _message = T("common.ready");
        var webServer = services.WebServers.Get(services.Settings.Current.WebServer);
        _databaseComponent = new(T("component.database.name"), T("component.database.description"), "mariadb", isRuntimeComponent: true);
        Components =
        [
            new(webServer.DisplayName, T("component.webserver.description"), webServer.Id, isRuntimeComponent: true),
            _databaseComponent,
            new(webServer.PhpRuntimeProcessId is null ? "PHP" : "PHP FastCGI", T("component.php.description"),
                webServer.PhpRuntimeProcessId ?? "php", packageId: "php", isProcessBacked: webServer.PhpRuntimeProcessId is not null)
        ];
        StartAllCommand = new AsyncRelayCommand(_ => RunServersAsync(ServerOperation.Starting));
        StopAllCommand = new AsyncRelayCommand(_ => RunServersAsync(ServerOperation.Stopping));
        StopComponentCommand = new AsyncRelayCommand(StopComponentAsync, static parameter => parameter is ComponentCardViewModel { CanStop: true });
        RestartComponentCommand = new AsyncRelayCommand(RestartComponentAsync, static parameter => parameter is ComponentCardViewModel { CanRestart: true });
        OpenRootCommand = new RelayCommand(_ => OpenFolder(_services.Paths.AppRoot));
        OpenWwwCommand = new RelayCommand(_ => OpenFolder(_services.Paths.Resolve("www")));
        OpenLocalhostCommand = new RelayCommand(_ => OpenUrl(GetLocalUrl()));
        OpenPhpMyAdminCommand = new RelayCommand(_ => OpenUrl(GetLocalUrl(_phpMyAdminPath)));
        OpenPowerShellCommand = new AsyncRelayCommand(OpenPowerShellAsync);
        OpenProjectCommand = new RelayCommand(OpenProject, static parameter => parameter is LocalProject);
        OpenProjectFolderCommand = new RelayCommand(OpenProjectFolder, static parameter => parameter is LocalProject);
        _services.Processes.StatusChanged += OnStatusChanged;
        _ = RefreshAsync();
    }

    public ObservableCollection<ComponentCardViewModel> Components { get; }
    public ObservableCollection<LocalProject> FavoriteProjects { get; } = [];
    public AsyncRelayCommand StartAllCommand { get; }
    public AsyncRelayCommand StopAllCommand { get; }
    public AsyncRelayCommand StopComponentCommand { get; }
    public AsyncRelayCommand RestartComponentCommand { get; }
    public RelayCommand OpenRootCommand { get; }
    public RelayCommand OpenWwwCommand { get; }
    public RelayCommand OpenLocalhostCommand { get; }
    public RelayCommand OpenPhpMyAdminCommand { get; }
    public AsyncRelayCommand OpenPowerShellCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand OpenProjectFolderCommand { get; }
    public Visibility EmptyFavoriteProjectsVisibility => FavoriteProjects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public bool ShowQuickLocalhost => _services.Settings.Current.ShowQuickLocalhost;
    public bool ShowQuickPhpMyAdmin => _services.Settings.Current.ShowQuickPhpMyAdmin;
    public bool ShowQuickPowerShell => _services.Settings.Current.ShowQuickPowerShell;
    public bool ShowQuickWww => _services.Settings.Current.ShowQuickWww;
    public bool ShowQuickAppRoot => _services.Settings.Current.ShowQuickAppRoot;
    public bool IsAnyServerRunning
    {
        get
        {
            var serverIds = _services.WebServers.All.Select(server => server.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _services.Processes.GetStatuses().Any(status =>
        (serverIds.Contains(status.Id) || status.Id is "mariadb" or "mysql")
        && status.State is (ComponentState.Running or ComponentState.Starting));
        }
    }
    public bool IsAnyServerTransitioning
    {
        get
        {
            var serverIds = _services.WebServers.All.Select(server => server.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return _services.Processes.GetStatuses().Any(status =>
                (serverIds.Contains(status.Id) || status.Id is "mariadb" or "mysql")
                && status.State is ComponentState.Starting or ComponentState.Stopping);
        }
    }
    public bool AreServersStarting => _serverOperation == ServerOperation.Starting
        || (_serverOperation == ServerOperation.None && _services.Processes.GetStatuses()
            .Any(status => IsManagedServer(status.Id) && status.State == ComponentState.Starting));
    public bool AreServersStopping => _serverOperation == ServerOperation.Stopping
        || (_serverOperation == ServerOperation.None && _services.Processes.GetStatuses()
            .Any(status => IsManagedServer(status.Id) && status.State == ComponentState.Stopping));
    public Visibility StartAllVisibility => !IsAnyServerRunning && !AreServersStarting && !AreServersStopping
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility StopAllVisibility => IsAnyServerRunning && !AreServersStarting && !AreServersStopping
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility StartingServersVisibility => AreServersStarting ? Visibility.Visible : Visibility.Collapsed;
    public Visibility StoppingServersVisibility => AreServersStopping ? Visibility.Visible : Visibility.Collapsed;
    public bool IsPhpMyAdminInstalled
    {
        get => _isPhpMyAdminInstalled;
        private set
        {
            if (Set(ref _isPhpMyAdminInstalled, value)) Raise(nameof(IsPhpMyAdminVisible));
        }
    }
    public bool IsPhpMyAdminVisible => IsPhpMyAdminInstalled && ShowQuickPhpMyAdmin;

    public string Message
    {
        get => _message;
        private set => Set(ref _message, value);
    }

    private async Task RunAsync(Func<Task> action, string successMessage)
    {
        try
        {
            Message = T("common.pleaseWait");
            await action();
            Message = successMessage;
        }
        catch (Exception exception)
        {
            Message = exception.Message;
            _services.Logs.Write("Application", exception.Message, LogSeverity.Error);
        }
    }

    private void OnStatusChanged(object? sender, ProcessStatus status)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var component = Components.FirstOrDefault(item => item.Id == status.Id);
            if (component is not null)
            {
                component.UpdateState(status.State, StateText(status.State));
            }
            if (status.State == ComponentState.Failed && status.Error is not null) Message = status.Error;
            Raise(nameof(IsAnyServerRunning));
            Raise(nameof(IsAnyServerTransitioning));
            Raise(nameof(AreServersStarting));
            Raise(nameof(AreServersStopping));
            Raise(nameof(StartAllVisibility));
            Raise(nameof(StopAllVisibility));
            Raise(nameof(StartingServersVisibility));
            Raise(nameof(StoppingServersVisibility));
            StopComponentCommand.RaiseCanExecuteChanged();
            RestartComponentCommand.RaiseCanExecuteChanged();
        });
    }

    public async Task RefreshAsync()
    {
        Raise(nameof(ShowQuickLocalhost));
        Raise(nameof(ShowQuickPhpMyAdmin));
        Raise(nameof(ShowQuickPowerShell));
        Raise(nameof(ShowQuickWww));
        Raise(nameof(ShowQuickAppRoot));
        Raise(nameof(IsPhpMyAdminVisible));
        Raise(nameof(IsAnyServerRunning));
        Raise(nameof(IsAnyServerTransitioning));
        Raise(nameof(AreServersStarting));
        Raise(nameof(AreServersStopping));
        Raise(nameof(StartAllVisibility));
        Raise(nameof(StopAllVisibility));
        Raise(nameof(StartingServersVisibility));
        Raise(nameof(StoppingServersVisibility));
        var projects = await _services.Projects.GetProjectsAsync();
        FavoriteProjects.Clear();
        foreach (var project in projects.Where(static item => item.IsFavorite)) FavoriteProjects.Add(project);
        Raise(nameof(EmptyFavoriteProjectsVisibility));
        var webServer = _services.WebServers.Get(_services.Settings.Current.WebServer);
        Components[0].UpdateIdentity(webServer.DisplayName, T("component.webserver.description"), webServer.Id,
            webServer.Id, isRuntimeComponent: true);
        Components[2].UpdateIdentity(webServer.PhpRuntimeProcessId is null ? "PHP" : "PHP FastCGI",
            T("component.php.description"), webServer.PhpRuntimeProcessId ?? "php", "php",
            isProcessBacked: webServer.PhpRuntimeProcessId is not null);
        var installed = await _services.Packages.GetInstalledAsync();
        var database = installed.FirstOrDefault(static item => item.Id == "mysql" && item.IsActive)
            ?? installed.FirstOrDefault(static item => item.Id == "mariadb" && item.IsActive);
        _databaseComponent.UpdateIdentity(
            database?.Id == "mysql" ? "MySQL" : database?.Id == "mariadb" ? "MariaDB" : T("component.database.name"),
            T("component.database.description"),
            database?.Id ?? "mariadb", database?.Id ?? "mariadb", isRuntimeComponent: true);
        var phpMyAdmin = installed.FirstOrDefault(static item => item.Id == "phpmyadmin" && item.IsActive);
        IsPhpMyAdminInstalled = phpMyAdmin is not null;
        if (phpMyAdmin is not null)
        {
            var manifest = (await _services.Packages.GetManifestsAsync()).FirstOrDefault(static item => item.Id == "phpmyadmin");
            _phpMyAdminPath = manifest?.Releases.FirstOrDefault(item => item.Version == phpMyAdmin.Version)?.LaunchPath?.TrimStart('/') ?? "phpmyadmin/";
        }
        var statuses = _services.Processes.GetStatuses().ToDictionary(static item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var component in Components)
        {
            var package = installed.FirstOrDefault(item => item.Id.Equals(component.PackageId, StringComparison.OrdinalIgnoreCase) && item.IsActive);
            component.Version = package?.Version ?? T("state.notInstalled");
            component.IsInstalled = package is not null;
            if (component.Id == "php") component.State = package is null ? T("state.notInstalled") : T("state.active");
            else if (statuses.TryGetValue(component.Id, out var status)) component.UpdateState(status.State, StateText(status.State));
            else component.UpdateState(ComponentState.Stopped, T("state.stopped"));
        }
        StopComponentCommand.RaiseCanExecuteChanged();
        RestartComponentCommand.RaiseCanExecuteChanged();
    }

    private async Task RunServersAsync(ServerOperation operation)
    {
        _serverOperation = operation;
        RaiseServerControlState();
        try
        {
            await RunAsync(
                operation == ServerOperation.Starting
                    ? () => _services.Runtime.StartAllAsync()
                    : () => _services.Runtime.StopAllAsync(),
                T(operation == ServerOperation.Starting
                    ? "dashboard.status.startedAll"
                    : "dashboard.status.stoppedAll"));
        }
        finally
        {
            _serverOperation = ServerOperation.None;
            RaiseServerControlState();
        }
    }

    private void RaiseServerControlState()
    {
        Raise(nameof(IsAnyServerRunning));
        Raise(nameof(IsAnyServerTransitioning));
        Raise(nameof(AreServersStarting));
        Raise(nameof(AreServersStopping));
        Raise(nameof(StartAllVisibility));
        Raise(nameof(StopAllVisibility));
        Raise(nameof(StartingServersVisibility));
        Raise(nameof(StoppingServersVisibility));
    }

    private bool IsManagedServer(string componentId) =>
        componentId is "mariadb" or "mysql"
        || _services.WebServers.All.Any(server => server.Id.Equals(componentId, StringComparison.OrdinalIgnoreCase));

    private async Task StopComponentAsync(object? parameter)
    {
        var component = (ComponentCardViewModel)parameter!;
        await RunAsync(
            () => _services.Runtime.StopAsync(component.Id),
            TF("dashboard.status.componentStopped", component.Name));
    }

    private async Task RestartComponentAsync(object? parameter)
    {
        var component = (ComponentCardViewModel)parameter!;
        await RunAsync(async () =>
        {
            if (component.RuntimeState is ComponentState.Running or ComponentState.Starting)
            {
                await _services.Runtime.StopAsync(component.Id);
            }
            await _services.Runtime.StartAsync(component.Id);
        }, TF("dashboard.status.componentRestarted", component.Name));
    }

    private static void OpenProject(object? parameter) => OpenUrl(((LocalProject)parameter!).Url);

    private static void OpenProjectFolder(object? parameter) => OpenFolder(((LocalProject)parameter!).DirectoryPath);

    private static void OpenFolder(string path)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    private async Task OpenPowerShellAsync(object? parameter)
    {
        try
        {
            var workingDirectory = parameter is LocalProject project ? project.DirectoryPath : null;
            Message = await PowerShellLauncher.OpenAsync(_services, workingDirectory);
        }
        catch (Exception exception)
        {
            Message = exception.Message;
            _services.Logs.Write("Application", exception.Message, LogSeverity.Error);
        }
    }

    private string GetLocalUrl(string relativePath = "")
    {
        var port = _services.Settings.Current.HttpPort;
        return $"http://localhost{(port == 80 ? "" : $":{port}")}/{relativePath}";
    }

    private static string StateText(ComponentState state) => state switch
    {
        ComponentState.Running => Localization.Get("state.running"),
        ComponentState.Starting => Localization.Get("state.starting"),
        ComponentState.Stopping => Localization.Get("state.stopping"),
        ComponentState.Failed => Localization.Get("state.failed"),
        _ => Localization.Get("state.stopped")
    };
}

public enum ServerOperation
{
    None,
    Starting,
    Stopping
}

public sealed class ComponentCardViewModel(string name, string description, string id, string? initialState = null,
    string? packageId = null, bool isRuntimeComponent = false, bool isProcessBacked = false) : ViewModelBase
{
    private string _name = name;
    private string _description = description;
    private string _id = id;
    private string _packageId = packageId ?? id;
    private bool _isRuntimeComponent = isRuntimeComponent;
    private bool _isProcessBacked = isProcessBacked || isRuntimeComponent;
    private string _state = initialState ?? Localization.Get("state.stopped");
    private string _version = Localization.Get("state.notInstalled");
    private bool _isInstalled;
    private ComponentState _runtimeState = ComponentState.Stopped;
    public string Name => _name;
    public string Description => _description;
    public string Id => _id;
    public string PackageId => _packageId;
    public string State { get => _state; set => Set(ref _state, value); }
    public bool IsRuntimeComponent => _isRuntimeComponent;
    public bool CanStop => IsRuntimeComponent && RuntimeState is ComponentState.Running or ComponentState.Starting;
    public bool CanRestart => IsRuntimeComponent && IsInstalled && RuntimeState is not (ComponentState.Starting or ComponentState.Stopping);
    public bool IsProcessBacked => _isProcessBacked;
    public bool IsOperational => IsProcessBacked ? RuntimeState == ComponentState.Running : IsInstalled;
    public bool IsTransitioning => IsProcessBacked && RuntimeState is ComponentState.Starting or ComponentState.Stopping;
    public ComponentState RuntimeState => _runtimeState;
    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (!Set(ref _isInstalled, value)) return;
            Raise(nameof(CanRestart));
            Raise(nameof(IsOperational));
        }
    }
    public string Version
    {
        get => _version;
        set
        {
            if (Set(ref _version, value)) Raise(nameof(VersionText));
        }
    }
    public string VersionText => string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("component.versionFormat"), Version);

    public void UpdateIdentity(string name, string description, string id, string? packageId = null,
        bool isRuntimeComponent = false, bool isProcessBacked = false)
    {
        if (Set(ref _name, name, nameof(Name))) Raise(nameof(VersionText));
        Set(ref _description, description, nameof(Description));
        Set(ref _packageId, packageId ?? id, nameof(PackageId));
        _isRuntimeComponent = isRuntimeComponent;
        _isProcessBacked = isProcessBacked || isRuntimeComponent;
        if (Set(ref _id, id, nameof(Id)))
        {
            Raise(nameof(IsRuntimeComponent));
            Raise(nameof(IsProcessBacked));
            Raise(nameof(CanStop));
            Raise(nameof(CanRestart));
            Raise(nameof(IsOperational));
            Raise(nameof(IsTransitioning));
        }
        Raise(nameof(IsRuntimeComponent));
        Raise(nameof(IsProcessBacked));
    }

    public void UpdateState(ComponentState state, string displayText)
    {
        _runtimeState = state;
        State = displayText;
        Raise(nameof(RuntimeState));
        Raise(nameof(CanStop));
        Raise(nameof(CanRestart));
        Raise(nameof(IsOperational));
        Raise(nameof(IsTransitioning));
    }
}


