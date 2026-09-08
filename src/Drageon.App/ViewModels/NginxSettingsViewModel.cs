using Drageon.Core;
using Drageon.Infrastructure;

namespace Drageon.App.ViewModels;

public sealed class NginxSettingsViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly string? _requestedVersion;
    private string? _version;
    private string _workers = "1", _connections = "1024", _logLevel = "warn", _custom = "", _status = "";
    public NginxSettingsViewModel(AppServices services, string? version = null)
    {
        _services = services; _requestedVersion = version;
        LogLevels = ["error", "warn", "notice", "info", "debug"];
        SaveCommand = new(_ => SaveAsync(false)); SaveAndRestartCommand = new(_ => SaveAsync(true)); _ = LoadAsync();
    }
    public IReadOnlyList<string> LogLevels { get; }
    public string WorkerProcesses { get => _workers; set => Set(ref _workers, value); }
    public string WorkerConnections { get => _connections; set => Set(ref _connections, value); }
    public string LogLevel { get => _logLevel; set => Set(ref _logLevel, value); }
    public string CustomConfiguration { get => _custom; set => Set(ref _custom, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string VersionLabel => _version is null ? "" : $"· Nginx {_version}";
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand SaveAndRestartCommand { get; }
    private async Task LoadAsync()
    {
        _version = _requestedVersion ?? await VersionedComponentConfiguration.GetActiveVersionAsync(_services.Paths, _services.Store, "nginx"); Raise(nameof(VersionLabel));
        if (_version is null) { Status = T("nginx.status.activateFirst"); return; }
        var value = await VersionedComponentConfiguration.LoadAsync(_services.Paths, _services.Store, "nginx", _version, static () => new NginxSettings());
        WorkerProcesses = value.WorkerProcesses.ToString(); WorkerConnections = value.WorkerConnections.ToString(); LogLevel = value.LogLevel; CustomConfiguration = value.CustomConfiguration;
    }
    private async Task SaveAsync(bool restart)
    {
        if (_version is null || !int.TryParse(WorkerProcesses, out var workers) || workers is < 1 or > 64
            || !int.TryParse(WorkerConnections, out var connections) || connections is < 128 or > 65535)
        { Status = T("nginx.error.values"); return; }
        await _services.Store.WriteAsync(VersionedComponentConfiguration.GetSettingsPath(_services.Paths, "nginx", _version),
            new NginxSettings { WorkerProcesses = workers, WorkerConnections = connections, LogLevel = LogLevel, CustomConfiguration = CustomConfiguration });
        var active = await VersionedComponentConfiguration.GetActiveVersionAsync(_services.Paths, _services.Store, "nginx");
        if (_version.Equals(active, StringComparison.OrdinalIgnoreCase)) await _services.Generators.OfType<NginxConfigGenerator>().Single().GenerateAsync();
        var running = _services.Processes.GetStatuses().Any(item => item.Id == "nginx" && item.State == ComponentState.Running);
        if (restart && running && _version.Equals(active, StringComparison.OrdinalIgnoreCase)) { await _services.Runtime.StopAsync("nginx"); await _services.Runtime.StartAsync("nginx"); Status = T("nginx.status.savedRestarted"); }
        else Status = T("nginx.status.saved");
    }
}
