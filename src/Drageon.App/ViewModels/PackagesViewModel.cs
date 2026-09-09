using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Drageon.Core;
using Drageon.Infrastructure;

namespace Drageon.App.ViewModels;

public sealed class PackagesViewModel : ViewModelBase, IRefreshable, IDisposable
{
    private readonly AppServices _services;
    private string _status;

    public PackagesViewModel(AppServices services)
    {
        _services = services;
        _status = T("packages.status.loadingManifests");
        GroupedComponents = CollectionViewSource.GetDefaultView(Components);
        GroupedComponents.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PackageComponentViewModel.CategoryName)));
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        VersionActionCommand = new AsyncRelayCommand(VersionActionAsync, static parameter => parameter is PackageComponentViewModel);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, static parameter => parameter is PackageComponentViewModel);
        DeleteComponentCommand = new AsyncRelayCommand(DeleteComponentAsync, static parameter => parameter is PackageComponentViewModel);
        StartStopCommand = new AsyncRelayCommand(StartStopAsync, static parameter => parameter is PackageComponentViewModel { ShowStartStop: true });
        OpenCommand = new RelayCommand(Open, static parameter => parameter is PackageComponentViewModel);
        OpenDownloadPageCommand = new RelayCommand(OpenDownloadPage, static parameter => parameter is PackageComponentViewModel { CanOpenDownloadPage: true });
        ConfigureCommand = new RelayCommand(Configure, static parameter => parameter is PackageComponentViewModel);
        NewManifestCommand = new RelayCommand(_ => OpenManifestEditor());
        EditManifestCommand = new RelayCommand(EditManifest, static parameter => parameter is PackageComponentViewModel);
        _services.Processes.StatusChanged += OnProcessStatusChanged;
        _ = RefreshAsync();
    }

    public ObservableCollection<PackageComponentViewModel> Components { get; } = [];
    public ICollectionView GroupedComponents { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand VersionActionCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand DeleteComponentCommand { get; }
    public AsyncRelayCommand StartStopCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand OpenDownloadPageCommand { get; }
    public RelayCommand ConfigureCommand { get; }
    public RelayCommand NewManifestCommand { get; }
    public RelayCommand EditManifestCommand { get; }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public async Task RefreshAsync()
    {
        try
        {
            var manifests = await _services.Packages.GetManifestsAsync();
            var installed = await _services.Packages.GetInstalledAsync();
            var statuses = _services.Processes.GetStatuses().ToDictionary(static item => item.Id, StringComparer.OrdinalIgnoreCase);
            Components.Clear();

            foreach (var manifest in manifests
                         .OrderBy(manifest => CategoryOrder(NormalizeCategory(manifest.Category)))
                         .ThenBy(manifest => manifest.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            {
                var isWebServer = _services.WebServers.TryGet(manifest.Id, out _);
                var isSelectedWebServer = manifest.Id.Equals(_services.Settings.Current.WebServer, StringComparison.OrdinalIgnoreCase);
                var localPackages = installed.Where(item => item.Id == manifest.Id).ToArray();
                var options = manifest.Releases
                    .Select(release => new PackageVersionOption(
                        release.Version,
                        release,
                        localPackages.Any(item => item.Version == release.Version),
                        localPackages.Any(item => item.Version == release.Version && item.IsActive),
                        localPackages.Any(item => item.Version == release.Version && item.IsActive)
                            && (!isWebServer || isSelectedWebServer)))
                    .ToList();

                foreach (var local in localPackages.Where(item => options.All(option => option.Version != item.Version)))
                {
                    options.Add(new PackageVersionOption(local.Version, null, true, local.IsActive,
                        local.IsActive && (!isWebServer || isSelectedWebServer)));
                }

                var state = statuses.GetValueOrDefault(manifest.Id)?.State ?? ComponentState.Stopped;
                var isDatabase = manifest.Category.Equals("database", StringComparison.OrdinalIgnoreCase);
                var category = NormalizeCategory(manifest.Category);
                Components.Add(new PackageComponentViewModel(manifest, options, state,
                    Localization.Get($"packages.category.{category}", category),
                    isWebServer || isDatabase, !isWebServer || isSelectedWebServer, isWebServer, isSelectedWebServer));
            }

            Status = TF("packages.status.available", Components.Count);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    public void Dispose() => _services.Processes.StatusChanged -= OnProcessStatusChanged;

    private async Task VersionActionAsync(object? parameter)
    {
        var component = (PackageComponentViewModel)parameter!;
        var option = component.SelectedVersion;
        if (option is null) return;
        try
        {
            if (option.IsActive)
            {
                await ChangeActivationAsync(component, () => _services.Packages.DeactivateAsync(component.Manifest.Id), false);
                Status = TF("packages.status.deactivated", component.Name);
                await RefreshAsync();
                return;
            }

            if (!option.IsInstalled)
            {
                if (option.Release is null) throw new InvalidOperationException(T("packages.error.noUrl"));
                Status = TF("packages.status.installing", component.Name, option.Version);
                var progress = new Progress<double>(value => Status = TF("packages.status.progress", value));
                await _services.Packages.InstallAsync(component.Manifest, option.Release, progress);
            }

            await ChangeActivationAsync(component, () => _services.Packages.ActivateAsync(component.Manifest.Id, option.Version), true);
            Status = TF("packages.status.active", component.Name, option.Version);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            _services.Logs.Write(component.Name, exception.Message, LogSeverity.Error);
        }
    }

    private async Task DeleteAsync(object? parameter)
    {
        var component = (PackageComponentViewModel)parameter!;
        var option = component.SelectedVersion;
        if (option is not { IsInstalled: true } || !component.CanDelete) return;

        var confirmationKey = component.Manifest.Category.Equals("database", StringComparison.OrdinalIgnoreCase)
            ? "packages.confirmDeleteDatabaseVersion"
            : "packages.confirmDeleteVersion";
        var replacement = option.IsActive
            ? component.Versions.FirstOrDefault(item => item.IsInstalled
                && !item.Version.Equals(option.Version, StringComparison.OrdinalIgnoreCase))
            : null;
        var message = TF(confirmationKey, component.Name, option.Version);
        if (option.IsActive)
        {
            message += "\n\n" + (replacement is null
                ? TF("packages.confirmDeleteLastActiveVersion", component.Name)
                : TF("packages.confirmDeleteActiveVersion", replacement.Version));
        }
        var confirmation = System.Windows.MessageBox.Show(
            message,
            T("packages.deleteVersion"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirmation != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            if (option.IsActive)
            {
                if (replacement is null) await _services.Packages.DeactivateAsync(component.Manifest.Id);
                else await _services.Packages.ActivateAsync(component.Manifest.Id, replacement.Version);
            }
            await _services.Packages.DeleteAsync(component.Manifest.Id, option.Version);
            Status = TF("packages.status.deleted", component.Name, option.Version);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            _services.Logs.Write(component.Name, exception.Message, LogSeverity.Error);
        }
    }

    private async Task DeleteComponentAsync(object? parameter)
    {
        var component = (PackageComponentViewModel)parameter!;
        if (!component.CanDeleteComponent) return;

        var confirmation = System.Windows.MessageBox.Show(
            TF("packages.confirmDeleteComponent", component.Name),
            T("packages.deleteComponent"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (confirmation != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            await _services.Packages.DeleteComponentAsync(component.Manifest.Id);
            Status = TF("packages.status.componentDeleted", component.Name);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            _services.Logs.Write(component.Name, exception.Message, LogSeverity.Error);
        }
    }

    private async Task StartStopAsync(object? parameter)
    {
        var component = (PackageComponentViewModel)parameter!;
        if (!component.ShowStartStop) return;
        var wasRunning = component.IsRunning;
        try
        {
            Status = wasRunning ? TF("packages.status.stopping", component.Name) : TF("packages.status.starting", component.Name);
            if (wasRunning) await _services.Runtime.StopAsync(component.Manifest.Id);
            else
            {
                var option = component.SelectedVersion;
                if (option is not { IsInstalled: true })
                {
                    Status = T("packages.status.installFirst");
                    return;
                }

                await StopExclusiveGroupAsync(component.Manifest);
                if (!option.IsActive) await _services.Packages.ActivateAsync(component.Manifest.Id, option.Version);
                await _services.Runtime.StartAsync(component.Manifest.Id);
            }
            var successMessage = wasRunning
                ? TF("packages.status.stopped", component.Name)
                : TF("packages.status.started", component.Name);
            await RefreshAsync();
            Status = successMessage;
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            _services.Logs.Write(component.Name, exception.Message, LogSeverity.Error);
        }
    }

    private void Open(object? parameter)
    {
        var component = (PackageComponentViewModel)parameter!;
        var port = _services.Settings.Current.HttpPort;
        var launchPath = component.ActiveVersion?.Release?.LaunchPath?.TrimStart('/') ?? "phpmyadmin/";
        var target = component.Manifest.Id == "phpmyadmin" ? $"http://localhost{(port == 80 ? "" : $":{port}")}/{launchPath}" : null;
        if (target is not null) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
    }

    private static void OpenDownloadPage(object? parameter)
    {
        var component = (PackageComponentViewModel)parameter!;
        if (component.Manifest.DownloadPageUrl is not { Length: > 0 } url) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private async Task ChangeActivationAsync(PackageComponentViewModel component, Func<Task> change, bool activating)
    {
        var webServer = _services.Settings.Current.WebServer;
        var restartWebServer = component.Manifest.Id is "php" or "phpmyadmin"
            && _services.Processes.GetStatuses().Any(status =>
                status.Id.Equals(webServer, StringComparison.OrdinalIgnoreCase)
                && status.State is ComponentState.Running or ComponentState.Starting);

        var restartComponent = component.CanStart && component.IsRunning;
        if (restartWebServer) await _services.Runtime.StopAsync(webServer);
        if (restartComponent) await _services.Runtime.StopAsync(component.Manifest.Id);
        if (activating)
        {
            await StopExclusiveGroupAsync(component.Manifest);
        }
        try
        {
            await change();
        }
        finally
        {
            if (restartComponent && activating) await _services.Runtime.StartAsync(component.Manifest.Id);
            if (restartWebServer && (activating || component.Manifest.Id != "php"
                || !_services.WebServers.Get(webServer).RequiredPackageIds.Contains("php", StringComparer.OrdinalIgnoreCase)))
                await _services.Runtime.StartAsync(webServer);
        }
    }

    private async Task StopExclusiveGroupAsync(PackageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.ExclusiveGroup)) return;
        foreach (var other in Components.Where(item =>
                     !item.Manifest.Id.Equals(manifest.Id, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(item.Manifest.ExclusiveGroup, manifest.ExclusiveGroup, StringComparison.OrdinalIgnoreCase)))
        {
            var isRunning = _services.Processes.GetStatuses().Any(status =>
                status.Id.Equals(other.Manifest.Id, StringComparison.OrdinalIgnoreCase)
                && status.State is ComponentState.Running or ComponentState.Starting);
            if (isRunning) await _services.Runtime.StopAsync(other.Manifest.Id);
        }
    }

    private static string NormalizeCategory(string category) => category.Trim().ToLowerInvariant() switch
    {
        "webserver" => "webserver",
        "php" => "php",
        "database" => "database",
        "developmenttool" => "developmentTool",
        _ => "other"
    };

    private static int CategoryOrder(string category) => category switch
    {
        "webserver" => 0,
        "php" => 1,
        "database" => 2,
        "developmentTool" => 3,
        _ => 4
    };

    private void Configure(object? parameter)
    {
        var component = (PackageComponentViewModel)parameter!;
        if (component.Manifest.Id == "php")
        {
            var configuredVersion = component.SelectedVersion is { IsInstalled: true } selected
                ? selected.Version
                : component.ActiveVersion?.Version;
            new PhpSettingsWindow
            {
                DataContext = new PhpViewModel(_services, configuredVersion),
                Owner = System.Windows.Application.Current.MainWindow
            }.Show();
        }
        else if (component.Manifest.Id == "apache")
        {
            var configuredVersion = component.SelectedVersion is { IsInstalled: true } selected
                ? selected.Version
                : component.ActiveVersion?.Version;
            new ApacheSettingsWindow
            {
                DataContext = new ApacheSettingsViewModel(_services, configuredVersion),
                Owner = System.Windows.Application.Current.MainWindow
            }.Show();
        }
        else if (component.Manifest.Id == "nginx")
        {
            var configuredVersion = component.SelectedVersion is { IsInstalled: true } selected
                ? selected.Version : component.ActiveVersion?.Version;
            new NginxSettingsWindow
            {
                DataContext = new NginxSettingsViewModel(_services, configuredVersion),
                Owner = System.Windows.Application.Current.MainWindow
            }.Show();
        }
        else if (component.Manifest.Id == "phpmyadmin")
        {
            var configuredVersion = component.SelectedVersion is { IsInstalled: true } selected
                ? selected.Version
                : component.ActiveVersion?.Version;
            new PhpMyAdminSettingsWindow
            {
                DataContext = new PhpMyAdminSettingsViewModel(_services, configuredVersion),
                Owner = System.Windows.Application.Current.MainWindow
            }.Show();
        }
    }

    private void EditManifest(object? parameter)
    {
        if (parameter is PackageComponentViewModel component) OpenManifestEditor(component.Manifest);
    }

    private void OpenManifestEditor(PackageManifest? manifest = null)
    {
        var window = new PackageManifestEditorWindow(new PackageManifestEditorViewModel(_services, manifest))
        {
            Owner = System.Windows.Application.Current.MainWindow
        };
        if (window.ShowDialog() == true) _ = RefreshAsync();
    }

    private void OnProcessStatusChanged(object? sender, ProcessStatus status)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            Components.FirstOrDefault(item => item.Manifest.Id.Equals(status.Id, StringComparison.OrdinalIgnoreCase))?.UpdateState(status.State);
            if (status.State == ComponentState.Failed && status.Error is not null) Status = status.Error;
        });
    }
}

public sealed class PackageComponentViewModel : ViewModelBase
{
    private PackageVersionOption? _selectedVersion;
    private ComponentState _state;

    private readonly bool _showStartStop;
    private readonly bool _isWebServer;
    private readonly bool _isSelectedWebServer;

    public PackageComponentViewModel(PackageManifest manifest, IReadOnlyList<PackageVersionOption> versions,
        ComponentState state, string categoryName, bool canStart, bool showStartStop, bool isWebServer, bool isSelectedWebServer)
    {
        Manifest = manifest;
        CategoryName = categoryName;
        Versions = new ObservableCollection<PackageVersionOption>(versions
            .OrderByDescending(static item => item.IsActive)
            .ThenByDescending(static item => item.Version));
        _selectedVersion = Versions.FirstOrDefault(static item => item.IsActive)
            ?? Versions.FirstOrDefault();
        _state = state;
        CanStart = canStart;
        _showStartStop = showStartStop;
        _isWebServer = isWebServer;
        _isSelectedWebServer = isSelectedWebServer;
    }

    public PackageManifest Manifest { get; }
    public string CategoryName { get; }
    public ObservableCollection<PackageVersionOption> Versions { get; }
    public string Name => Manifest.Bundled
        ? Localization.Get($"package.{Manifest.Id}.name", Manifest.DisplayName)
        : Manifest.DisplayName;
    public string Description => Manifest.Bundled
        ? Localization.Get($"package.{Manifest.Id}.description", Manifest.Description)
        : Manifest.Description;
    public bool CanStart { get; }
    public bool ShowStartStop => CanStart && _showStartStop && Versions.Any(static item => item.IsInstalled);
    public PackageVersionOption? ActiveVersion => Versions.FirstOrDefault(static item => item.IsActive);
    public bool CanOpen => Manifest.Id == "phpmyadmin" && ActiveVersion is not null;
    public bool CanOpenDownloadPage => Uri.TryCreate(Manifest.DownloadPageUrl, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https";
    public bool CanConfigure => Manifest.Id is "apache" or "nginx" or "php" or "phpmyadmin";
    public bool IsCustom => !Manifest.Bundled;
    public bool HasActiveVersion => Versions.Any(static item => item.IsActive);
    public bool IsRunning => _state == ComponentState.Running;
    public bool CanDelete => SelectedVersion is { IsInstalled: true } selected
        && _state is ComponentState.Stopped or ComponentState.Failed;
    public bool CanDeactivate => HasActiveVersion && _state is ComponentState.Stopped or ComponentState.Failed;
    public bool CanDeleteComponent => IsCustom && !Versions.Any(static item => item.IsInstalled)
        && _state is ComponentState.Stopped or ComponentState.Failed;
    public bool CanUseVersionAction => SelectedVersion is not null
        && (!CanStart || !IsRunning)
        && (!SelectedVersion.IsActive || CanDeactivate);
    public bool ShowVersionAction => !_isWebServer || SelectedVersion is { IsInstalled: false };
    public string VersionActionText => SelectedVersion is null
        ? Localization.Get("packages.noVersion")
        : SelectedVersion.IsActive
            ? Localization.Get("packages.deactivate")
            : SelectedVersion.IsInstalled
                ? Localization.Get("action.activate")
                : Localization.Get("action.install");
    public string VersionActionGlyph => SelectedVersion is null
        ? "\uE946"
        : SelectedVersion.IsActive
            ? "\uE711"
            : SelectedVersion.IsInstalled
                ? "\uE73E"
                : "\uE896";
    public string StartStopText => IsRunning ? Localization.Get("common.stop") : Localization.Get("common.start");
    public string StartStopGlyph => IsRunning ? "\uE73B" : "\uF5B0";
    public string StateText => !CanStart
        ? HasActiveVersion
            ? Localization.Get("state.active")
            : Versions.Any(static item => item.IsInstalled)
                ? Localization.Get("state.inactive")
                : Localization.Get("state.notInstalled")
        : _isWebServer && !_isSelectedWebServer
            ? Versions.Any(static item => item.IsInstalled) ? Localization.Get("state.inactive") : Localization.Get("state.notInstalled")
        : _state switch
    {
        ComponentState.Running => Localization.Get("state.running"),
        ComponentState.Starting => Localization.Get("state.starting"),
        ComponentState.Stopping => Localization.Get("state.stopping"),
        ComponentState.Failed => Localization.Get("state.failedSeeLogs"),
        _ => Localization.Get("state.stopped")
    };

    public PackageVersionOption? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (!Set(ref _selectedVersion, value)) return;
            Raise(nameof(CanOpen));
            Raise(nameof(CanDelete));
            Raise(nameof(CanUseVersionAction));
            Raise(nameof(ShowVersionAction));
            Raise(nameof(VersionActionText));
            Raise(nameof(VersionActionGlyph));
        }
    }

    public void UpdateState(ComponentState state)
    {
        _state = state;
        Raise(nameof(IsRunning));
        Raise(nameof(CanDeactivate));
        Raise(nameof(CanUseVersionAction));
        Raise(nameof(VersionActionText));
        Raise(nameof(CanDeleteComponent));
        Raise(nameof(StartStopText));
        Raise(nameof(StartStopGlyph));
        Raise(nameof(StateText));
    }
}

public sealed record PackageVersionOption(string Version, PackageRelease? Release, bool IsInstalled, bool IsActive, bool ShowAsActive)
{
    public string DisplayText => ShowAsActive
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("packages.version.active"), Version)
        : IsInstalled
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("packages.version.installed"), Version)
            : Version;
    public override string ToString() => DisplayText;
}


