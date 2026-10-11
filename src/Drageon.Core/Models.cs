using System.Text.Json;
using System.Text.Json.Serialization;

namespace Drageon.Core;

public sealed record AppSettings
{
    public string WebServer { get; init; } = "apache";
    public int FastCgiPort { get; init; } = 9000;
    public string Language { get; init; } = "de";
    public bool StartComponentsWithApp { get; init; }
    public bool CloseToTrayOnWindowClose { get; init; } = true;
    public int HttpPort { get; init; } = 80;
    public int HttpsPort { get; init; } = 443;
    public int SmtpPort { get; init; } = 1025;
    public bool NotifyOnMailReceived { get; init; } = true;
    public bool PlaySoundOnMailReceived { get; init; }
    public bool NotifyOnCronJobFinished { get; init; } = true;
    public bool PlaySoundOnCronJobFinished { get; init; }
    public bool ShowQuickLocalhost { get; init; } = true;
    public bool ShowQuickPhpMyAdmin { get; init; } = true;
    public bool ShowQuickPowerShell { get; init; } = true;
    public bool ShowQuickWww { get; init; } = true;
    public bool ShowQuickAppRoot { get; init; } = true;
}

public sealed record PackageManifest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public string Description { get; init; } = "";
    public string Category { get; init; } = "other";
    public bool Bundled { get; init; }
    public string? ExclusiveGroup { get; init; }
    public string? DownloadPageUrl { get; init; }
    public string InstallDirectory { get; init; } = "";
    public bool AddToPath { get; init; }
    public IReadOnlyList<string> PathDirectories { get; init; } = [];
    public IReadOnlyList<PackageRelease> Releases { get; init; } = [];
}

public static class PackageCatalog
{
    public static bool IsDatabase(string packageId) =>
        packageId.Equals("mariadb", StringComparison.OrdinalIgnoreCase)
        || packageId.Equals("mysql", StringComparison.OrdinalIgnoreCase);
    public static string? OtherDatabase(string packageId) => packageId.ToLowerInvariant() switch
    {
        "mariadb" => "mysql",
        "mysql" => "mariadb",
        _ => null
    };
}

public sealed record VirtualHostDefinition(
    string Host,
    string DocumentRoot,
    bool Enabled = true,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Port = null)
{
    [JsonExtensionData]
    public IDictionary<string, JsonElement> ServerConfiguration { get; init; }
        = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    public string GetServerConfiguration(string serverId) =>
        ServerConfiguration.TryGetValue(serverId, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

public sealed record PackageRelease
{
    public required string Version { get; init; }
    public required string Url { get; init; }
    public string? Sha256 { get; init; }
    public string ArchiveType { get; init; } = "zip";
    public string? FileName { get; init; }
    public string? StripPrefix { get; init; }
    public string? LaunchPath { get; init; }
}

public sealed record InstalledPackage(
    string Id,
    string Version,
    string Directory,
    bool IsActive);

public enum ScheduledJobType
{
    PhpScript,
    Url,
    Curl,
    Command
}

public sealed record ScheduledJob
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public ScheduledJobType Type { get; init; }
    public required string Expression { get; init; }
    public required string Target { get; init; }
    public string Arguments { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public bool CatchUpMissedRun { get; init; }
    public DateTimeOffset? LastRunAt { get; init; }
}

public sealed record CronJobRunResult(Guid JobId, string JobName, DateTimeOffset FinishedAt, bool Success, string Message);

public sealed record ProcessDefinition(
    string Id,
    string ExecutablePath,
    string Arguments,
    string WorkingDirectory,
    string? LogFile = null,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null,
    bool KillChildrenOnExit = false);

public enum ComponentState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Failed
}

public sealed record ProcessStatus(
    string Id,
    ComponentState State,
    int? ProcessId = null,
    string? Error = null);

public sealed record LocalProject(
    string Id,
    string Name,
    string DirectoryPath,
    string RelativePath,
    string Url,
    string Type,
    IReadOnlyList<string> Technologies,
    bool IsFavorite,
    bool HasVirtualHost,
    string? DatabaseName = null,
    string? VirtualHost = null,
    string? StagingOf = null);

public sealed record ProjectMetadata
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Favorite { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Database { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Vhost { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StagingOf { get; init; }
    public string? StagingIdentity { get; init; }

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && !Favorite && string.IsNullOrWhiteSpace(Database) && string.IsNullOrWhiteSpace(Url)
        && string.IsNullOrWhiteSpace(Vhost) && string.IsNullOrWhiteSpace(StagingOf);
}

public sealed record DatabaseConnection(string Host = "127.0.0.1", int Port = 3306, string User = "root", string? Password = null);
public sealed record DatabaseServerInfo(string ComponentId, string DisplayName, string Version, string ClientPath, string DumpPath, DatabaseConnection Connection);
public sealed record DatabaseBackupInfo(string Path, string DatabaseName, DateTimeOffset CreatedAt, long Size)
{
    public string FileName => System.IO.Path.GetFileName(Path);
    public string SizeLabel => Size < 1024 * 1024 ? $"{Math.Max(1, Size / 1024)} KB" : $"{Size / 1024d / 1024d:F1} MB";
}
public sealed record ProjectSnapshotInfo(
    string DirectoryPath,
    string ProjectPath,
    string ProjectName,
    DateTimeOffset CreatedAt,
    string Description,
    string? DatabaseName,
    string? DatabaseType,
    string? DatabaseVersion,
    bool IncludesFiles = true,
    bool IncludesVirtualHost = false,
    bool IncludesMetadata = true);

public sealed record SnapshotFileEntry(string Path, string Kind, string? LinkTarget = null, long? Size = null, string? Sha256 = null, long? ModifiedUtcTicks = null);
public sealed record ProjectSnapshotManifest
{
    public int FormatVersion { get; init; }
    public string? ArchiveSha256 { get; init; }
    public long? ArchiveSize { get; init; }
    public string? IndexSha256 { get; init; }
    public long? DatabaseSize { get; init; }
    public string? DatabaseSha256 { get; init; }
    public string? DatabaseServerIdentity { get; init; }
    public required ProjectSnapshotInfo Snapshot { get; init; }
    public required IReadOnlyList<SnapshotFileEntry> Entries { get; init; }
    public ProjectMetadata? Metadata { get; init; }
    public VirtualHostDefinition? VirtualHost { get; init; }
}

public sealed record StagingRequest(
    string SourceProjectPath,
    string ProjectName,
    string TargetProjectPath,
    string Url,
    string? TargetDatabase,
    bool CopyDatabase,
    bool CreateVirtualHost);

public sealed record StagingResult(string ProjectPath, string? DatabaseName, string Url);
public sealed record OperationPlan(string ProjectPath, string? Server, string? TargetDatabase, IReadOnlyList<string> VirtualHosts, string BackupScope, IReadOnlyList<string> Snapshots, string Fingerprint);
public enum RecoveryOutcome { Unchanged, RolledBack, Completed, ManualRecoveryRequired }
public sealed class RecoveryOperationException(RecoveryOutcome outcome, string message, Exception? innerException = null) : IOException(message, innerException)
{
    public RecoveryOutcome Outcome { get; } = outcome;
}

public sealed record PhpSettings
{
    public string MemoryLimit { get; init; } = "256M";
    public string UploadMaxFilesize { get; init; } = "64M";
    public string PostMaxSize { get; init; } = "64M";
    public int MaxExecutionTime { get; init; } = 120;
    public bool DisplayErrors { get; init; } = true;
    public bool OpcacheEnabled { get; init; } = true;
    public IReadOnlyList<string> Extensions { get; init; } = ["curl", "mbstring", "openssl", "pdo_mysql"];
    public string CustomIni { get; init; } = "";
}

public sealed record ApacheSettings
{
    public string ServerName { get; init; } = "localhost";
    public bool AllowHtaccess { get; init; } = true;
    public bool DirectoryListing { get; init; } = true;
    public int ThreadsPerChild { get; init; } = 32;
    public string LogLevel { get; init; } = "warn";
    public string CustomConfiguration { get; init; } = "";
}

public sealed record NginxSettings
{
    public int WorkerProcesses { get; init; } = 1;
    public int WorkerConnections { get; init; } = 1024;
    public string LogLevel { get; init; } = "warn";
    public string CustomConfiguration { get; init; } = "";
}

public sealed record PhpMyAdminSettings
{
    public string AuthType { get; init; } = "cookie";
    public string DatabaseHost { get; init; } = "127.0.0.1";
    public int DatabasePort { get; init; } = 3306;
    public bool AllowNoPassword { get; init; } = true;
    public int LoginCookieValidity { get; init; } = 1440;
    public int MaxRows { get; init; } = 50;
    public bool ShowServerInfo { get; init; } = true;
    public string BlowfishSecret { get; init; } = "";
    public string CustomConfiguration { get; init; } = "";
}

public sealed record MariaDbSettings
{
    public int Port { get; init; } = 3306;
    public string CharacterSet { get; init; } = "utf8mb4";
    public int MaxConnections { get; init; } = 100;
    public string BufferPoolSize { get; init; } = "64M";
    public string CustomConfiguration { get; init; } = "";
    public string DatabaseUser { get; init; } = "root";
    public string ProtectedPassword { get; init; } = "";
}

public sealed record MySqlSettings
{
    public int Port { get; init; } = 3306;
    public string CharacterSet { get; init; } = "utf8mb4";
    public int MaxConnections { get; init; } = 100;
    public string BufferPoolSize { get; init; } = "64M";
    public string CustomConfiguration { get; init; } = "";
    public string DatabaseUser { get; init; } = "root";
    public string ProtectedPassword { get; init; } = "";
}

public sealed record MailMessageSummary(
    Guid Id,
    DateTimeOffset ReceivedAt,
    string From,
    IReadOnlyList<string> To,
    string Subject,
    bool HasAttachments);

public sealed record CapturedMailMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;
    public string From { get; init; } = "";
    public IReadOnlyList<string> To { get; init; } = [];
    public string Subject { get; init; } = "";
    public string TextBody { get; init; } = "";
    public string HtmlBody { get; init; } = "";
    public string Headers { get; init; } = "";
    public IReadOnlyList<string> Attachments { get; init; } = [];
    public string RawFile { get; init; } = "";
}

public enum LogSeverity
{
    Information,
    Warning,
    Error
}

public sealed record LogEntry(
    DateTimeOffset Timestamp,
    string Component,
    string Message,
    LogSeverity Severity = LogSeverity.Information);
