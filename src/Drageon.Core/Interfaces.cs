namespace Drageon.Core;

public interface IPortablePathService
{
    string AppRoot { get; }
    string Resolve(params string[] segments);
    void EnsureDirectoryLayout();
}

public interface ISettingsService
{
    AppSettings Current { get; }
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ILanguageService
{
    string CurrentLanguage { get; }
    Task LoadAsync(string language, CancellationToken cancellationToken = default);
    string Get(string key, string? fallback = null);
}

public interface IPackageManager
{
    Task RefreshEnvironmentAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PackageManifest>> GetManifestsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<InstalledPackage>> GetInstalledAsync(CancellationToken cancellationToken = default);
    Task InstallAsync(PackageManifest manifest, PackageRelease release, IProgress<double>? progress = null, CancellationToken cancellationToken = default);
    Task ActivateAsync(string packageId, string version, CancellationToken cancellationToken = default);
    Task DeactivateAsync(string packageId, CancellationToken cancellationToken = default);
    Task DeleteAsync(string packageId, string version, CancellationToken cancellationToken = default);
    Task DeleteComponentAsync(string packageId, CancellationToken cancellationToken = default);
}

public interface IProcessManager : IAsyncDisposable
{
    event EventHandler<ProcessStatus>? StatusChanged;
    IReadOnlyCollection<ProcessStatus> GetStatuses();
    Task<ProcessStatus> StartAsync(ProcessDefinition definition, CancellationToken cancellationToken = default);
    Task StopAsync(string id, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task StopAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IConfigGenerator
{
    string ComponentId { get; }
    Task<string> GenerateAsync(CancellationToken cancellationToken = default);
}

public interface IRuntimeController
{
    Task StartAsync(string componentId, CancellationToken cancellationToken = default);
    Task StopAsync(string componentId, CancellationToken cancellationToken = default);
    Task StartAllAsync(CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
}

public interface IWebServer
{
    string Id { get; }
    string DisplayName { get; }
    IReadOnlyList<string> RequiredPackageIds { get; }
    string? PhpRuntimeProcessId { get; }
    bool UsesFastCgiPort { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IWebServerRegistry
{
    IReadOnlyCollection<IWebServer> All { get; }
    bool TryGet(string id, out IWebServer webServer);
    IWebServer Get(string id);
}

public interface IProjectDiscoveryService
{
    Task<IReadOnlyList<LocalProject>> GetProjectsAsync(CancellationToken cancellationToken = default);
    Task SetFavoriteAsync(string projectId, bool isFavorite, CancellationToken cancellationToken = default);
}

public interface IAppLogService
{
    event EventHandler<LogEntry>? EntryAdded;
    IReadOnlyList<LogEntry> GetRecent(int maximum = 1000);
    void Write(string component, string message, LogSeverity severity = LogSeverity.Information);
    void Clear();
}

public interface ICronScheduler : IAsyncDisposable
{
    event EventHandler<CronJobRunResult>? JobFinished;
    bool IsRunning { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task ReloadAsync(CancellationToken cancellationToken = default);
    Task RunNowAsync(Guid jobId, CancellationToken cancellationToken = default);
}

public interface ISmtpCatcher : IAsyncDisposable
{
    event EventHandler<CapturedMailMessage>? MessageReceived;
    bool IsRunning { get; }
    int Port { get; }
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CapturedMailMessage>> GetMessagesAsync(CancellationToken cancellationToken = default);
}
