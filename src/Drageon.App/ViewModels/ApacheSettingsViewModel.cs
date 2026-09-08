using Drageon.Core;
using Drageon.Infrastructure;

namespace Drageon.App.ViewModels;

public sealed class ApacheSettingsViewModel : ViewModelBase, IRefreshable
{
    private readonly AppServices _services;
    private readonly string? _requestedVersion;
    private string? _configuredVersion;
    private string _serverName = "localhost";
    private bool _allowHtaccess = true;
    private bool _directoryListing = true;
    private string _threadsPerChild = "32";
    private string _logLevel = "warn";
    private string _customConfiguration = "";
    private string _status = "";

    public ApacheSettingsViewModel(AppServices services, string? version = null)
    {
        _services = services;
        _requestedVersion = version;
        LogLevels = ["error", "warn", "notice", "info", "debug"];
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync(false));
        SaveAndRestartCommand = new AsyncRelayCommand(_ => SaveAsync(true));
        _ = RefreshAsync();
    }

    public IReadOnlyList<string> LogLevels { get; }
    public string ServerName { get => _serverName; set => Set(ref _serverName, value); }
    public bool AllowHtaccess { get => _allowHtaccess; set => Set(ref _allowHtaccess, value); }
    public bool DirectoryListing { get => _directoryListing; set => Set(ref _directoryListing, value); }
    public string ThreadsPerChild { get => _threadsPerChild; set => Set(ref _threadsPerChild, value); }
    public string LogLevel { get => _logLevel; set => Set(ref _logLevel, value); }
    public string CustomConfiguration { get => _customConfiguration; set => Set(ref _customConfiguration, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string VersionLabel => _configuredVersion is null ? "" : $"· Apache {_configuredVersion}";
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveAndRestartCommand { get; }

    public async Task RefreshAsync()
    {
        try
        {
            _configuredVersion = _requestedVersion ?? await GetActiveVersionAsync();
            Raise(nameof(VersionLabel));
            if (_configuredVersion is null)
            {
                Status = T("apache.status.activateFirst");
                return;
            }

            var settings = await VersionedComponentConfiguration.LoadAsync(
                _services.Paths, _services.Store, "apache", _configuredVersion, static () => new ApacheSettings());
            ServerName = settings.ServerName;
            AllowHtaccess = settings.AllowHtaccess;
            DirectoryListing = settings.DirectoryListing;
            ThreadsPerChild = settings.ThreadsPerChild.ToString(System.Globalization.CultureInfo.InvariantCulture);
            LogLevel = settings.LogLevel;
            CustomConfiguration = settings.CustomConfiguration;
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private async Task SaveAsync(bool restart)
    {
        if (!int.TryParse(ThreadsPerChild, out var threads) || threads is < 8 or > 256)
        {
            Status = T("apache.error.threads");
            return;
        }
        if (string.IsNullOrWhiteSpace(ServerName))
        {
            Status = T("apache.error.serverName");
            return;
        }
        var version = _configuredVersion ?? _requestedVersion ?? await GetActiveVersionAsync();
        if (version is null)
        {
            Status = T("apache.status.activateFirst");
            return;
        }

        var settings = new ApacheSettings
        {
            ServerName = ServerName.Trim(),
            AllowHtaccess = AllowHtaccess,
            DirectoryListing = DirectoryListing,
            ThreadsPerChild = threads,
            LogLevel = LogLevel,
            CustomConfiguration = CustomConfiguration
        };
        await _services.Store.WriteAsync(
            VersionedComponentConfiguration.GetSettingsPath(_services.Paths, "apache", version), settings);
        var activeVersion = await GetActiveVersionAsync();
        await _services.Generators.OfType<ApacheConfigGenerator>().Single().GenerateForVersionAsync(version);

        var wasRunning = _services.Processes.GetStatuses().Any(static item => item.Id.Equals("apache", StringComparison.OrdinalIgnoreCase)
            && item.State == ComponentState.Running);
        if (restart && wasRunning && version.Equals(activeVersion, StringComparison.OrdinalIgnoreCase))
        {
            await _services.Runtime.StopAsync("apache");
            await _services.Runtime.StartAsync("apache");
            Status = T("apache.status.savedRestarted");
        }
        else
        {
            Status = wasRunning && version.Equals(activeVersion, StringComparison.OrdinalIgnoreCase)
                ? T("apache.status.savedNeedsRestart")
                : TF("apache.status.savedVersion", version);
        }
    }

    private async Task<string?> GetActiveVersionAsync()
    {
        var active = await _services.Store.ReadAsync<Dictionary<string, string>>(
            _services.Paths.Resolve("app", "config", "packages.active.json")) ?? [];
        return active.GetValueOrDefault("apache");
    }
}

