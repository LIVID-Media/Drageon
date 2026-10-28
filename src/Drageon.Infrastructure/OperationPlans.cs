using Drageon.Core;
using System.Security.Cryptography;
using System.Text;

namespace Drageon.Infrastructure;

public sealed partial class ProjectSnapshotService
{
    public async Task DeleteProjectSnapshotAsync(string snapshotDirectory, CancellationToken cancellationToken = default, string? expectedProjectPath = null, string? confirmedPlan = null)
    {
        await OperationSafety.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await DeleteProjectSnapshotCoreAsync(snapshotDirectory, cancellationToken, expectedProjectPath, confirmedPlan).ConfigureAwait(false); }
        finally { OperationSafety.Gate.Release(); }
    }

    public async Task RecoverFileExchangeAsync(string journalPath, string expectedProjectPath, CancellationToken cancellationToken = default)
    {
        await OperationSafety.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = paths.Resolve(NormalizeProjectPath(expectedProjectPath).Split('/'));
            var journal = Path.GetFullPath(journalPath);
            OperationSafety.NoLinks(journal);
            if (Path.GetDirectoryName(journal) != Path.GetDirectoryName(target) || !Path.GetFileName(journal).StartsWith(Path.GetFileName(target) + ".operation-", StringComparison.Ordinal)) throw new InvalidDataException("Ungültiges Operationsjournal.");
            var state = System.Text.Json.JsonSerializer.Deserialize<FileExchange>(File.ReadAllText(journal)) ?? throw new InvalidDataException("Operationszustand fehlt.");
            var suffix = journal[(target + ".operation-").Length..^5];
            if (!Guid.TryParseExact(suffix, "N", out _) || state.Target != target || state.Prepared != target + ".prepare-" + suffix || state.Previous != target + ".previous-" + suffix) throw new InvalidDataException("Manipulierter Operationszustand.");
            OperationSafety.NoLinks(target); OperationSafety.NoLinks(state.Previous); OperationSafety.NoLinks(state.Prepared);
            cancellationToken.ThrowIfCancellationRequested();
            // Recovery deliberately does not use the user token after this boundary.
            if (Directory.Exists(state.Previous))
            {
                if (state.PreviousIdentity is null || OperationSafety.DirectoryIdentity(state.Previous) != state.PreviousIdentity) throw new IOException($"Manuelle Prüfung erforderlich: Rückfallordner ohne passenden Erzeugungszustand: {state.Previous}");
                if (Directory.Exists(target))
                {
                    if (state.PreparedIdentity is null || OperationSafety.DirectoryIdentity(target) != state.PreparedIdentity) throw new IOException($"Unverändert abgebrochen: Fremdes Wiederherstellungsziel bleibt erhalten: {target}; alter Stand {state.Previous}");
                    if (Directory.Exists(state.Prepared)) throw new IOException($"Manuelle Wiederherstellung erforderlich: fremdes Ziel oder widersprüchlicher Zustand. Alter Stand: {state.Previous}");
                    OperationSafety.MoveDirectory(target, state.Prepared);
                }
                OperationSafety.MoveDirectory(state.Previous, target);
            }
            else if (state.HadPrevious && !Directory.Exists(target)) throw new IOException($"Manuelle Wiederherstellung erforderlich: {state.Previous}; {journal}");
            else if (state.HadPrevious && (state.PreviousIdentity is null || OperationSafety.DirectoryIdentity(target) != state.PreviousIdentity)) throw new IOException($"Manuelle Wiederherstellung erforderlich: Alter Stand fehlt; Ziel bleibt erhalten: {target}; {state.Previous}");
            else if (!state.HadPrevious && Directory.Exists(target))
            {
                if (state.PreparedIdentity is null || OperationSafety.DirectoryIdentity(target) != state.PreparedIdentity || Directory.Exists(state.Prepared)) throw new IOException($"Manuelle Prüfung erforderlich: Fremdes oder widersprüchliches Ziel bleibt erhalten: {target}");
                OperationSafety.MoveDirectory(target, state.Prepared);
            }
            if (Directory.Exists(target))
            {
                var metadata = await projects.GetMetadataAsync(expectedProjectPath, CancellationToken.None).ConfigureAwait(false);
                if (metadata?.StagingIdentity is not null)
                {
                    var ownershipFile = paths.Resolve("app", "config", "staging", OperationSafety.Relative(metadata.StagingIdentity) + ".json");
                    OperationSafety.NoLinks(ownershipFile);
                    var ownership = await store.ReadAsync<StagingOwnership>(ownershipFile).ConfigureAwait(false);
                    if (ownership?.Identity == metadata.StagingIdentity && ownership.ProjectPath == NormalizeProjectPath(expectedProjectPath)
                        && (ownership.RootIdentity == state.PreviousIdentity || ownership.RootIdentity == state.PreparedIdentity))
                        OperationSafety.Persist(ownershipFile, ownership with { RootIdentity = OperationSafety.DirectoryIdentity(target) });
                }
            }
            OperationSafety.Persist(journal, state with { Phase = "rolled-back" });
        }
        finally { OperationSafety.Gate.Release(); }
    }
    public async Task<ProjectSnapshotInfo> CreateProjectSnapshotAsync(string projectPath, string description, DatabaseConnection? connection = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await OperationSafety.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await CreateProjectSnapshotCoreAsync(projectPath, description, connection, progress, cancellationToken).ConfigureAwait(false); }
        finally { OperationSafety.Gate.Release(); }
    }

    private void ValidateProjectBinding(string directory, ProjectSnapshotManifest manifest, string? expected)
    {
        var project = NormalizeProjectPath(manifest.Snapshot.ProjectPath);
        var folder = Path.GetFileName(Path.GetDirectoryName(directory)!);
        if (!folder.Equals(Path.GetFileName(project), StringComparison.OrdinalIgnoreCase) ||
            (expected is not null && !project.Equals(NormalizeProjectPath(expected), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Snapshot gehört nicht zum erwarteten Projekt oder Fundort.");
    }

    public async Task<OperationPlan> GetSnapshotPlanAsync(string snapshotDirectory, string expectedProjectPath, bool delete, CancellationToken cancellationToken = default)
    {
        var directory = ValidateSnapshotDirectory(snapshotDirectory);
        var manifest = await store.ReadAsync<ProjectSnapshotManifest>(Path.Combine(directory, "snapshot.json"), cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Snapshot-Metadaten fehlen.");
        ValidateProjectBinding(directory, manifest, expectedProjectPath);
        if (!delete)
        {
            var entries = await ArchiveFileTree.ReadEntries(directory, manifest, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => ArchiveFileTree.Validate(directory, manifest, entries, verifyArchiveContent: false), cancellationToken).ConfigureAwait(false);
            await ValidateRestoreResources(directory, manifest, NormalizeProjectPath(expectedProjectPath), cancellationToken).ConfigureAwait(false);
        }
        var target = paths.Resolve(NormalizeProjectPath(expectedProjectPath).Split('/'));
        var fingerprint = await Task.Run(() => PlanFingerprint(paths.AppRoot, target, directory, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (!delete && manifest.Snapshot.DatabaseName is string databaseName)
        {
            using var database = await databases.AcquireSnapshotDatabaseAsync(databaseName, manifest.DatabaseServerIdentity, null, cancellationToken).ConfigureAwait(false);
            fingerprint += database.Fingerprint;
        }
        return new(target, manifest.DatabaseServerIdentity ?? manifest.Snapshot.DatabaseType, manifest.Snapshot.DatabaseName,
            manifest.VirtualHost is null ? [] : [manifest.VirtualHost.Host],
            delete ? "none" : "snapshotRestore", delete ? [ToPortable(directory)] : [], fingerprint);
    }

    internal static void CheckPlan(string confirmed, OperationPlan current)
    {
        if (confirmed != current.Fingerprint) throw new RecoveryOperationException(RecoveryOutcome.Unchanged, "Unverändert abgebrochen: Operationsplan wurde geändert. Erneute Bestätigung erforderlich.");
    }

    internal static string PlanFingerprint(string appRoot, string project, string? snapshot, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        void Tree(string directory)
        {
            cancellationToken.ThrowIfCancellationRequested();
            text.AppendLine(directory);
            if (!Directory.Exists(directory)) { text.AppendLine("missing"); return; }
            OperationSafety.NoLinks(directory);
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                text.AppendLine(item.FullName);
                if (item.LinkTarget is not null) text.AppendLine(item.LinkTarget);
                else if (item is DirectoryInfo child) Tree(child.FullName);
                else if (snapshot is not null && item.FullName.Equals(Path.Combine(snapshot, "files.zip"), StringComparison.OrdinalIgnoreCase))
                    text.AppendLine($"{new FileInfo(item.FullName).Length}:{item.LastWriteTimeUtc.Ticks}");
                else text.AppendLine(OperationSafety.Hash(item.FullName));
            }
        }
        Tree(project);
        Tree(Path.Combine(appRoot, "app", "config", "vhosts"));
        Tree(Path.Combine(appRoot, "app", "config", "staging"));
        var metadata = Path.Combine(appRoot, "app", "config", "projects.json");
        OperationSafety.NoLinks(metadata);
        if (File.Exists(metadata)) text.AppendLine(OperationSafety.Hash(metadata));
        if (snapshot is not null) Tree(snapshot);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

public sealed partial class StagingService
{
    public async Task<OperationPlan> GetDeletePlanAsync(string stagingProjectPath, bool deleteDatabase, DatabaseConnection? connection = null, CancellationToken cancellationToken = default)
    {
        var relative = new ProjectMetadataStore(paths, store).NormalizeProjectPath(stagingProjectPath);
        var metadata = await projects.GetMetadataAsync(relative, cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Staging-Metadaten fehlen.");
        var server = deleteDatabase && metadata.Database is not null ? await databases.GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false) : null;
        var snapshotList = await snapshots.GetProjectSnapshotsAsync(relative, cancellationToken).ConfigureAwait(false);
        var target = paths.Resolve(relative.Split('/'));
        var databaseFingerprint = "";
        if (server is not null)
        {
            if (metadata.StagingIdentity is null) throw new InvalidOperationException("Staging-Datenbanknachweis fehlt.");
            var proofPath = paths.Resolve("app", "config", "staging", OperationSafety.Relative(metadata.StagingIdentity) + ".json");
            OperationSafety.NoLinks(proofPath);
            var proof = await store.ReadAsync<StagingOwnership>(proofPath, cancellationToken).ConfigureAwait(false);
            if (proof is null || proof.Identity != metadata.StagingIdentity || proof.ProjectPath != relative || proof.CreatedDatabase != metadata.Database || proof.DatabaseIdentity is null || proof.ServerIdentity is null)
                throw new InvalidOperationException("Staging-Datenbank wurde neu zugeordnet oder ihr Erzeugungsnachweis fehlt; unverändert abgebrochen.");
            using var operation = await databases.AcquireOwnedDatabaseAsync(metadata.Database!, proof.ServerIdentity, proof.DatabaseIdentity, connection, cancellationToken).ConfigureAwait(false);
            databaseFingerprint = await databases.GetInstanceIdentityAsync(server, cancellationToken).ConfigureAwait(false)
                + System.Text.Json.JsonSerializer.Serialize(await databases.GetDatabaseStorageIdentityAsync(server, metadata.Database!, cancellationToken).ConfigureAwait(false));
        }
        return new(target, server is null ? null : $"{server.DisplayName} {server.Version} {server.Connection.Host}:{server.Connection.Port}", metadata.Database,
            metadata.Vhost is null ? [] : [metadata.Vhost], server is null ? "staging" : "stagingDatabase",
            snapshotList.Select(item => item.DirectoryPath).ToArray(),
            ProjectSnapshotService.PlanFingerprint(paths.AppRoot, target, paths.Resolve("data", "snapshots", Path.GetFileName(relative)), cancellationToken)
                + $"|{server?.ClientPath}|{server?.Connection.Host}|{server?.Connection.Port}|{deleteDatabase}|{databaseFingerprint}");
    }
}

public sealed partial class DatabaseBackupService
{
    public async Task RecoverDatabaseAsync(string journalPath, string expectedDatabase, DatabaseConnection? connection = null, CancellationToken cancellationToken = default)
    {
        ValidateDatabaseName(expectedDatabase);
        var journal = Path.GetFullPath(journalPath);
        OperationSafety.NoLinks(journal);
        var root = paths.Resolve("data", "backups", "databases");
        var journalNamePattern = "^" + System.Text.RegularExpressions.Regex.Escape(expectedDatabase)
            + @"(?:_recovery_[a-fA-F0-9]{32}|_\d{4}-\d{2}-\d{2}_\d{6}_\d{3}-recovery)\.sql\.operation\.json$";
        if (Path.GetDirectoryName(journal) != root || !System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(journal), journalNamePattern)) throw new InvalidDataException("Ungültiges Datenbank-Journal.");
        var state = System.Text.Json.JsonSerializer.Deserialize<DatabaseRecovery>(File.ReadAllText(journal)) ?? throw new InvalidDataException("Operationszustand fehlt.");
        var backup = journal[..^".operation.json".Length];
        if (state.Database != expectedDatabase || state.Backup != backup) throw new InvalidDataException("Rückfalldump gehört nicht zur erwarteten Datenbank.");
        OperationSafety.NoLinks(backup);
        if (state.BackupSha256 is null || OperationSafety.Hash(backup) != state.BackupSha256) throw new InvalidDataException("Rückfalldump: Integritätsfehler.");
        var server = await GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false);
        if (await GetInstanceIdentityAsync(server, cancellationToken).ConfigureAwait(false) != state.ServerIdentity) throw new InvalidOperationException("Unverändert abgebrochen: Serverkonfiguration/Instanz wurde geändert.");
        ValidateSqlToken(state.CharacterSet ?? throw new InvalidDataException("Schema-Eigenschaften fehlen."));
        if (state.Collation is not null) ValidateSqlToken(state.Collation);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var currentBackup = backup + $".current-{Guid.NewGuid():N}.sql";
        try
        {
            if ((await GetDatabasesAsync(server.Connection, cancellationToken).ConfigureAwait(false)).Contains(expectedDatabase, StringComparer.OrdinalIgnoreCase))
                await DumpToPathCoreAsync(server, expectedDatabase, currentBackup, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await RunClientQueryAsync(server, $"DROP DATABASE IF EXISTS {QuoteIdentifier(expectedDatabase)}; CREATE DATABASE {QuoteIdentifier(expectedDatabase)} CHARACTER SET {state.CharacterSet}" + (state.Collation is null ? ";" : $" COLLATE {state.Collation};"), CancellationToken.None).ConfigureAwait(false);
            await ImportFromPathCoreAsync(server, expectedDatabase, backup, CancellationToken.None).ConfigureAwait(false);
            OperationSafety.Persist(journal, state with { Phase = "rolled-back" });
        }
        catch (Exception exception)
        {
            throw new IOException($"Manuelle Wiederherstellung erforderlich: {backup}; aktueller Rückfalldump {currentBackup}; Zustand {journal}. {exception.Message}", exception);
        }
        finally { _operationGate.Release(); }
    }

    public async Task<OperationPlan> GetRestorePlanAsync(string backupPath, string targetDatabase, DatabaseConnection? connection = null, CancellationToken cancellationToken = default)
    {
        ValidateDatabaseName(targetDatabase);
        var backup = ValidateSqlPath(backupPath);
        var server = await GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false);
        var names = await GetDatabasesAsync(server.Connection, cancellationToken).ConfigureAwait(false);
        var instance = await GetInstanceIdentityAsync(server, cancellationToken).ConfigureAwait(false);
        var fingerprint = $"{instance}|{server.ComponentId}|{server.Version}|{server.ClientPath}|{server.Connection.Host}|{server.Connection.Port}|{server.Connection.User}|{targetDatabase}|{OperationSafety.Hash(backup)}|{string.Join(',', names)}";
        return new(backup, $"{server.DisplayName} {server.Version} {server.Connection.Host}:{server.Connection.Port}", targetDatabase, [], "database", [], fingerprint);
    }
}
