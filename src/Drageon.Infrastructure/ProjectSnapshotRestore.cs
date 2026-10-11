using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Drageon.Core;

namespace Drageon.Infrastructure;

internal sealed record SnapshotReceipt(string Project, string Directory, string ManifestHash);
internal sealed record ProjectRestoreState(string Project, string Prepared, string Recovery, string Phase,
    ProjectMetadata? BeforeMetadata, ProjectMetadata? AfterMetadata, string? HostPath, string? BeforeHostHash, string? AfterHostHash,
    SnapshotDatabaseState? Database = null, string? OwnershipPath = null, StagingOwnership? Ownership = null, string? RecoveryIdentity = null, string? PreparedIdentity = null);

public sealed partial class ProjectSnapshotService
{
    private string ReceiptPath(string directory) => paths.Resolve("app", "config", "snapshots", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ToPortable(directory)))) + ".json");

    private async Task ValidateRestoreResources(string directory, ProjectSnapshotManifest manifest, string relative, CancellationToken token)
    {
        var target = paths.Resolve(relative.Split('/'));
        var all = await new ProjectMetadataStore(paths, store).ReadAndPruneAsync(token).ConfigureAwait(false);
        var current = all.GetValueOrDefault(relative);
        if (manifest.Metadata?.StagingIdentity != current?.StagingIdentity || manifest.Metadata?.StagingOf != current?.StagingOf)
            throw new IOException("Snapshot und Projekt haben unterschiedliche Staging-Zugehörigkeit.");
        if (manifest.Snapshot.DatabaseName is string database)
        {
            if (manifest.Metadata?.Database != database) throw new InvalidDataException("Snapshot-Datenbank und Projektmetadaten widersprechen sich.");
            var sql = Path.Combine(directory, "database.sql"); OperationSafety.NoLinks(sql);
            if (!File.Exists(sql)) throw new InvalidDataException("Erwarteter SQL-Dump fehlt.");
            if (manifest.FormatVersion >= 2 && (manifest.DatabaseSize is null || manifest.DatabaseSha256 is null || new FileInfo(sql).Length != manifest.DatabaseSize || OperationSafety.Hash(sql) != manifest.DatabaseSha256))
                throw new InvalidDataException("SQL-Dump: Integritätsfehler.");
            var receiptFile = ReceiptPath(directory); OperationSafety.NoLinks(receiptFile);
            var receipt = await store.ReadAsync<SnapshotReceipt>(receiptFile, token).ConfigureAwait(false);
            var proven = receipt?.Project == relative && receipt.Directory == ToPortable(directory) && receipt.ManifestHash == OperationSafety.Hash(Path.Combine(directory, "snapshot.json"));
            if (receipt is not null && !proven) throw new InvalidDataException("Snapshot und gespeicherter Erzeugungsnachweis widersprechen sich; Bestand bleibt unverändert.");
            if (!proven && current?.Database != database) throw new IOException("Für die frühere Zieldatenbank fehlt ein Snapshot-Erzeugungsnachweis; Bestand bleibt unverändert.");
            if (all.Any(pair => !pair.Key.Equals(relative, StringComparison.OrdinalIgnoreCase) && pair.Value.Database?.Equals(database, StringComparison.OrdinalIgnoreCase) == true))
                throw new IOException("Zieldatenbank wird von einem anderen Projekt verwendet; Bestand bleibt unverändert.");
        }
        if (manifest.VirtualHost is { } host)
        {
            var resolved = new VirtualHostStore(paths, store).Resolve(host);
            if (!OperationSafety.Contains(target, resolved.DocumentRoot) || manifest.Metadata?.Vhost != host.Host)
                throw new IOException("Snapshot-VHost gehört nicht zum Projekt.");
            OperationSafety.NoLinks(resolved.DocumentRoot);
            if (all.Any(pair => !pair.Key.Equals(relative, StringComparison.OrdinalIgnoreCase) && pair.Value.Vhost?.Equals(host.Host, StringComparison.OrdinalIgnoreCase) == true))
                throw new IOException("VHost wird von einem anderen Projekt verwendet.");
            var hostPath = paths.Resolve("app", "config", "vhosts", VHostFileName(host.Host)); OperationSafety.NoLinks(hostPath);
            var existing = await store.ReadAsync<VirtualHostDefinition>(hostPath, token).ConfigureAwait(false);
            if (existing is not null && (!existing.Host.Equals(host.Host, StringComparison.OrdinalIgnoreCase) || !OperationSafety.Contains(target, new VirtualHostStore(paths, store).Resolve(existing).DocumentRoot)))
                throw new IOException("VHost wurde einem anderen Projekt zugeordnet; Definition bleibt erhalten.");
        }
        else if (manifest.Metadata?.Vhost is not null) throw new IOException("Snapshot enthält eine VHost-Zuordnung ohne Definition.");
    }

    public async Task RestoreProjectSnapshotAsync(string snapshotDirectory, bool backupCurrentState = true, DatabaseConnection? connection = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default, string? expectedProjectPath = null, string? confirmedPlan = null)
    {
        await OperationSafety.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        DatabaseBackupService.SnapshotDatabaseOperation? database = null;
        string? prepared = null;
        ProjectRestoreState? state = null;
        string? journal = null;
        var started = false;
        try
        {
            var directory = ValidateSnapshotDirectory(snapshotDirectory);
            var manifest = await store.ReadAsync<ProjectSnapshotManifest>(Path.Combine(directory, "snapshot.json"), cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Snapshot-Metadaten fehlen.");
            ValidateProjectBinding(directory, manifest, expectedProjectPath);
            var relative = NormalizeProjectPath(manifest.Snapshot.ProjectPath);
            var target = paths.Resolve(relative.Split('/'));
            OperationSafety.NoLinks(target); OperationSafety.AssertNoPending(target);
            if (confirmedPlan is not null) CheckPlan(confirmedPlan, await GetSnapshotPlanAsync(snapshotDirectory, relative, false, cancellationToken).ConfigureAwait(false));
            var entries = await ArchiveFileTree.ReadEntries(directory, manifest, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => ArchiveFileTree.Validate(directory, manifest, entries), cancellationToken).ConfigureAwait(false);
            await ValidateRestoreResources(directory, manifest, relative, cancellationToken).ConfigureAwait(false);
            var metadataStore = new ProjectMetadataStore(paths, store);
            var before = (await metadataStore.ReadAndPruneAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(relative);
            StagingOwnership? ownership = null; string? ownershipPath = null;
            if (before?.StagingIdentity is string staging)
            {
                ownershipPath = paths.Resolve("app", "config", "staging", OperationSafety.Relative(staging) + ".json"); OperationSafety.NoLinks(ownershipPath);
                ownership = await store.ReadAsync<StagingOwnership>(ownershipPath, cancellationToken).ConfigureAwait(false);
                if (ownership?.Identity != staging || ownership.ProjectPath != relative || Directory.Exists(target) && ownership.RootIdentity != OperationSafety.DirectoryIdentity(target))
                    throw new IOException("Staging-Ordner ohne passenden Erzeugungsnachweis.");
            }
            if (manifest.Snapshot.DatabaseName is string name)
            {
                // Check support/server before an optional snapshot or preparation is created.
                using (await databases.AcquireSnapshotDatabaseAsync(name, manifest.DatabaseServerIdentity, connection, cancellationToken).ConfigureAwait(false)) { }
            }
            if (backupCurrentState && Directory.Exists(target))
                await CreateProjectSnapshotCoreAsync(relative, "Automatisch vor Wiederherstellung", connection, progress, cancellationToken).ConfigureAwait(false);
            progress?.Report("files");
            prepared = await Task.Run(() => ArchiveFileTree.Prepare(directory, target, manifest, entries, logs, cancellationToken, progress), cancellationToken).ConfigureAwait(false);
            var id = prepared[(target + ".prepare-").Length..];
            var recovery = paths.Resolve("data", "recovery", "restore-" + id);
            OperationSafety.CreateDirectoryExclusive(recovery);
            if (manifest.Snapshot.DatabaseName is not null)
            {
                progress?.Report("database");
                var sourceSql = Path.Combine(directory, "database.sql"); OperationSafety.NoLinks(sourceSql);
                var inputSql = Path.Combine(recovery, "database.input.sql");
                var input = await Task.Run(() => ArchiveFileTree.CopyFile(sourceSql, inputSql, cancellationToken), cancellationToken).ConfigureAwait(false);
                if ((manifest.DatabaseSize is not null && input.Size != manifest.DatabaseSize)
                    || manifest.DatabaseSha256 is not null && input.Hash != manifest.DatabaseSha256)
                    throw new InvalidDataException("Vorbereiteter SQL-Dump: Integritätsfehler.");
            }
            var hostPath = manifest.VirtualHost is null ? null : paths.Resolve("app", "config", "vhosts", VHostFileName(manifest.VirtualHost.Host));
            var beforeHostHash = hostPath is not null && File.Exists(hostPath) ? OperationSafety.Hash(hostPath) : null;
            if (beforeHostHash is not null) File.Copy(hostPath!, Path.Combine(recovery, "vhost.before.json"), false);
            if (manifest.VirtualHost is not null) await store.WriteAsync(Path.Combine(recovery, "vhost.after.json"), manifest.VirtualHost, cancellationToken).ConfigureAwait(false);
            state = new(relative, prepared, recovery, "prepared", before, manifest.Metadata, hostPath, beforeHostHash,
                manifest.VirtualHost is null ? null : OperationSafety.Hash(Path.Combine(recovery, "vhost.after.json")), OwnershipPath: ownershipPath, Ownership: ownership, RecoveryIdentity: OperationSafety.DirectoryIdentity(recovery), PreparedIdentity: OperationSafety.DirectoryIdentity(prepared));
            journal = target + ".restore-operation-" + id + ".json";
            void PersistDatabase(SnapshotDatabaseState value) { state = state with { Database = value }; OperationSafety.Persist(journal, state); }
            if (manifest.Snapshot.DatabaseName is string db)
            {
                database = await databases.AcquireSnapshotDatabaseAsync(db, manifest.DatabaseServerIdentity, connection, cancellationToken).ConfigureAwait(false);
                state = state with { Database = await database.BackupAsync(Path.Combine(recovery, "database.before.sql"), cancellationToken).ConfigureAwait(false) };
            }
            OperationSafety.Persist(journal, state);
            await ValidateRestoreResources(directory, manifest, relative, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            started = true;
            state = state with { Phase = "exchanging-files" }; OperationSafety.Persist(journal, state);
            SafeFileTree.Exchange(prepared, target, logs, cancellationToken, keepJournal: true);
            state = state with { Phase = "files-installed" }; OperationSafety.Persist(journal, state);
            cancellationToken.ThrowIfCancellationRequested();
            if (database is not null)
            {
                progress?.Report("database");
                await database.ApplyAsync(Path.Combine(recovery, "database.input.sql"), state.Database!, PersistDatabase, cancellationToken).ConfigureAwait(false);
            }
            OperationSafety.Checkpoint?.Invoke("snapshot-files-installed");
            progress?.Report("vhost");
            if (hostPath is not null)
            {
                if ((File.Exists(hostPath) ? OperationSafety.Hash(hostPath) : null) != beforeHostHash) throw new IOException("VHost wurde zwischenzeitlich geändert.");
                await store.WriteAsync(hostPath, manifest.VirtualHost!, cancellationToken, overwrite: beforeHostHash is not null).ConfigureAwait(false);
            }
            state = state with { Phase = "vhost-installed" }; OperationSafety.Persist(journal, state);
            OperationSafety.Checkpoint?.Invoke("snapshot-vhost-installed");
            progress?.Report("metadata");
            if (await projects.GetMetadataAsync(relative, cancellationToken).ConfigureAwait(false) != before) throw new IOException("Projektmetadaten wurden zwischenzeitlich geändert.");
            await metadataStore.SetWithinOperationAsync(relative, manifest.Metadata ?? new(), cancellationToken).ConfigureAwait(false);
            if (ownership is not null)
                OperationSafety.Persist(ownershipPath!, ownership with { RootIdentity = OperationSafety.DirectoryIdentity(target), DatabaseIdentity = state.Database?.Created ?? ownership.DatabaseIdentity,
                    VhostHash = ownership.VhostPath == hostPath ? state.AfterHostHash : ownership.VhostHash });
            cancellationToken.ThrowIfCancellationRequested();
            state = state with { Phase = "completed" }; OperationSafety.Persist(journal, state);
            ArchiveCompletedRestore(state, journal, target, id);
            logs.Write("Snapshot", $"Snapshot vollständig wiederhergestellt: {relative}. Alter Stand und Operationszustand: {recovery}");
        }
        catch (Exception original)
        {
            if (state?.Phase == "completed")
                throw new RecoveryOperationException(RecoveryOutcome.Completed, $"Wiederherstellung abgeschlossen; Bereinigung fehlgeschlagen. Sicherung: {state.Recovery}; Operationszustand: {journal}. {original.Message}", original);
            if (!started || state is null || journal is null)
            {
                if (state is not null && journal is not null) OperationSafety.Persist(journal, state with { Phase = "rolled-back" });
                throw new RecoveryOperationException(RecoveryOutcome.Unchanged, $"Unverändert abgebrochen: {original.Message}", original);
            }
            try
            {
                await RollbackProjectRestore(state, journal, database).ConfigureAwait(false);
                string cleanupDetail = "";
                try { CleanupRolledBackRestore(journal, paths.Resolve(state.Project.Split('/')), state); }
                catch (Exception cleanup)
                {
                    cleanupDetail = $" Bereinigung fehlgeschlagen: {prepared}; {journal}; {cleanup.Message}";
                    logs.Write("Snapshot", "Vorheriger Zustand wiederhergestellt;" + cleanupDetail, LogSeverity.Warning);
                }
                throw new RecoveryOperationException(RecoveryOutcome.RolledBack, $"Wiederherstellung fehlgeschlagen; vorheriger Zustand wiederhergestellt. {original.Message}{cleanupDetail}", original);
            }
            catch (RecoveryOperationException) { throw; }
            catch (Exception compensation)
            {
                throw new RecoveryOperationException(RecoveryOutcome.ManualRecoveryRequired, $"Manuelle Wiederherstellung erforderlich. Sicherungen: {state.Recovery}; alter Dateiordner: {state.Prepared.Replace(".prepare-", ".previous-", StringComparison.Ordinal)}; Dateizustand: {journal}. {original.Message}; Rückkehr fehlgeschlagen: {compensation.Message}", original);
            }
        }
        finally
        {
            database?.Dispose();
            if (!started && prepared is not null && Directory.Exists(prepared))
                try { SafeFileTree.Delete(prepared); } catch (Exception cleanup) { logs.Write("Snapshot", $"Vorbereitung erhalten: {prepared}; {cleanup.Message}", LogSeverity.Warning); }
            OperationSafety.Gate.Release();
        }
    }

    private static void CleanupRolledBackRestore(string journal, string target, ProjectRestoreState expected)
    {
        OperationSafety.NoLinks(journal);
        var state = JsonSerializer.Deserialize<ProjectRestoreState>(File.ReadAllText(journal)) ?? throw new InvalidDataException("Restore-Zustand fehlt.");
        if (state.Project != expected.Project || state.Prepared != expected.Prepared || state.Recovery != expected.Recovery
            || state.RecoveryIdentity != expected.RecoveryIdentity || state.PreparedIdentity != expected.PreparedIdentity) throw new IOException("Restore-Zustand wurde verändert; Bereinigung abgebrochen.");
        var id = state.Prepared[(target + ".prepare-").Length..];
        if (!Guid.TryParseExact(id, "N", out _) || state.Phase != "rolled-back") throw new IOException("Ungültiger Rückkehrzustand.");
        OperationSafety.NoLinks(state.Recovery);
        if (OperationSafety.DirectoryIdentity(state.Recovery) != state.RecoveryIdentity) throw new IOException("Fremder Rückfallordner bleibt erhalten.");
        var fileJournal = target + ".operation-" + id + ".json"; OperationSafety.NoLinks(fileJournal);
        if (File.Exists(fileJournal))
        {
            var exchange = JsonSerializer.Deserialize<FileExchange>(File.ReadAllText(fileJournal)) ?? throw new InvalidDataException("Dateizustand fehlt.");
            if (exchange.Target != target || exchange.Prepared != state.Prepared || exchange.Phase != "rolled-back"
                || exchange.HadPrevious && OperationSafety.DirectoryIdentity(target) != exchange.PreviousIdentity
                || !exchange.HadPrevious && Directory.Exists(target)) throw new IOException("Alter Stand wurde ersetzt; Bereinigung abgebrochen.");
            OperationSafety.NoLinks(state.Prepared);
            OperationSafety.Persist(Path.Combine(state.Recovery, "files.operation.json"), exchange);
            OperationSafety.Persist(Path.Combine(state.Recovery, "restore.operation.json"), state);
            if (Directory.Exists(state.Prepared))
            {
                if (OperationSafety.DirectoryIdentity(state.Prepared) != exchange.PreparedIdentity) throw new IOException("Fremder Vorbereitungsordner bleibt erhalten.");
                SafeFileTree.Delete(state.Prepared);
            }
            File.Delete(fileJournal);
        }
        else
        {
            OperationSafety.Persist(Path.Combine(state.Recovery, "restore.operation.json"), state);
            OperationSafety.NoLinks(state.Prepared);
            if (Directory.Exists(state.Prepared))
            {
                if (state.PreparedIdentity is null || OperationSafety.DirectoryIdentity(state.Prepared) != state.PreparedIdentity) throw new IOException("Fremder Vorbereitungsordner bleibt erhalten.");
                SafeFileTree.Delete(state.Prepared);
            }
        }
        File.Delete(journal);
    }

    private static void ArchiveCompletedRestore(ProjectRestoreState state, string journal, string target, string id)
    {
        OperationSafety.NoLinks(state.Recovery);
        if (OperationSafety.DirectoryIdentity(state.Recovery) != state.RecoveryIdentity) throw new IOException("Rückfallordner wurde ersetzt.");
        var fileJournal = target + ".operation-" + id + ".json";
        var savedJournal = Path.Combine(state.Recovery, "restore.operation.json");
        OperationSafety.NoLinks(fileJournal); OperationSafety.NoLinks(savedJournal);
        OperationSafety.Persist(savedJournal, state);
        if (File.Exists(fileJournal))
        {
            var exchange = JsonSerializer.Deserialize<FileExchange>(File.ReadAllText(fileJournal)) ?? throw new InvalidDataException("Dateizustand fehlt.");
            var previous = target + ".previous-" + id;
            var archived = Path.Combine(state.Recovery, "files.before");
            if (exchange.Target != target || exchange.Prepared != state.Prepared || exchange.Previous != previous || exchange.Phase != "completed") throw new IOException("Ungültiger Dateizustand.");
            OperationSafety.NoLinks(previous); OperationSafety.NoLinks(archived);
            if (exchange.HadPrevious && !Directory.Exists(previous)) throw new IOException($"Alter Dateiordner fehlt: {previous}");
            // Persist the destination before moving; recovery accepts either location after interruption.
            OperationSafety.Persist(Path.Combine(state.Recovery, "files.operation.json"), exchange with { Previous = archived });
            OperationSafety.Checkpoint?.Invoke("snapshot-archive-prepared");
            if (Directory.Exists(previous))
            {
                if (OperationSafety.DirectoryIdentity(previous) != exchange.PreviousIdentity) throw new IOException("Fremder Rückfallordner bleibt erhalten.");
                OperationSafety.MoveDirectory(previous, archived);
            }
            OperationSafety.Checkpoint?.Invoke("snapshot-archive-moved");
            File.Delete(fileJournal);
        }
        File.Delete(journal);
    }

    public async Task RecoverProjectRestoreAsync(string journalPath, string expectedProjectPath, DatabaseConnection? connection = null, CancellationToken cancellationToken = default)
    {
        await OperationSafety.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        DatabaseBackupService.SnapshotDatabaseOperation? database = null;
        try
        {
            var relative = NormalizeProjectPath(expectedProjectPath);
            var target = paths.Resolve(relative.Split('/'));
            var journal = Path.GetFullPath(journalPath); OperationSafety.NoLinks(journal);
            var prefix = target + ".restore-operation-";
            var archived = Path.GetFileName(journal) == "restore.operation.json";
            var id = archived ? Path.GetFileName(Path.GetDirectoryName(journal)!)["restore-".Length..] :
                journal.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && journal.EndsWith(".json", StringComparison.Ordinal) ? journal[prefix.Length..^5] : "";
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Ungültiger Operationsbezeichner.");
            if (archived && journal != paths.Resolve("data", "recovery", "restore-" + id, "restore.operation.json")) throw new InvalidDataException("Ungültiger Wiederherstellungszustand.");
            var state = JsonSerializer.Deserialize<ProjectRestoreState>(File.ReadAllText(journal)) ?? throw new InvalidDataException("Operationszustand fehlt.");
            if (state.Project != relative || state.Prepared != target + ".prepare-" + id || state.Recovery != paths.Resolve("data", "recovery", "restore-" + id)) throw new InvalidDataException("Manipulierte Operationspfade.");
            OperationSafety.NoLinks(state.Recovery);
            if (state.RecoveryIdentity is null || OperationSafety.DirectoryIdentity(state.Recovery) != state.RecoveryIdentity) throw new IOException("Rückfallordner wurde ersetzt; manuelle Prüfung erforderlich.");
            if (state.HostPath is not null)
            {
                var saved = Path.Combine(state.Recovery, "vhost.after.json"); OperationSafety.NoLinks(saved);
                if (OperationSafety.Hash(saved) != state.AfterHostHash) throw new IOException("VHost-Sicherung wurde verändert.");
                var host = await store.ReadAsync<VirtualHostDefinition>(saved, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("VHost-Sicherung fehlt.");
                if (state.HostPath != paths.Resolve("app", "config", "vhosts", VHostFileName(host.Host)) || !OperationSafety.Contains(target, new VirtualHostStore(paths, store).Resolve(host).DocumentRoot)) throw new InvalidDataException("Ungültiger VHost-Rückfallpfad.");
            }
            if (state.Ownership is not null && (state.Ownership.ProjectPath != relative || state.OwnershipPath != paths.Resolve("app", "config", "staging", OperationSafety.Relative(state.Ownership.Identity) + ".json"))) throw new InvalidDataException("Ungültiger Staging-Nachweis.");
            if (state.Database is not null)
            {
                if (state.Database.Name != state.AfterMetadata?.Database) throw new InvalidDataException("Ungültige Datenbank-Zuordnung im Operationszustand.");
                var all = await new ProjectMetadataStore(paths, store).ReadAndPruneAsync(cancellationToken).ConfigureAwait(false);
                if (all.Any(pair => pair.Key != relative && pair.Value.Database == state.Database.Name)) throw new IOException("Zieldatenbank wird inzwischen gemeinsam verwendet; manuelle Wiederherstellung erforderlich.");
                database = await databases.AcquireSnapshotDatabaseAsync(state.Database.Name, state.Database.Server, connection, cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await RollbackProjectRestore(state, journal, database).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new RecoveryOperationException(RecoveryOutcome.ManualRecoveryRequired, $"Manuelle Wiederherstellung erforderlich; Sicherungspfade im Operationszustand: {journalPath}. {exception.Message}", exception);
        }
        finally { database?.Dispose(); OperationSafety.Gate.Release(); }
    }

    private async Task RollbackProjectRestore(ProjectRestoreState state, string journal, DatabaseBackupService.SnapshotDatabaseOperation? database)
    {
        var errors = new List<string>();
        async Task Attempt(Func<Task> action) { try { await action().ConfigureAwait(false); } catch (Exception ex) { errors.Add(ex.Message); } }
        var target = paths.Resolve(state.Project.Split('/'));
        var id = state.Prepared[(target + ".prepare-").Length..];
        await Attempt(async () =>
        {
            if (state.Database is not null && database is not null)
                await database.RollbackAsync(state.Database, Path.Combine(state.Recovery, "database.before.sql"), value => { state = state with { Database = value }; OperationSafety.Persist(journal, state); }).ConfigureAwait(false);
        });
        await Attempt(() =>
        {
            var fileJournal = target + ".operation-" + id + ".json";
            var archivedJournal = Path.Combine(state.Recovery, "files.operation.json");
            if (!File.Exists(fileJournal)) fileJournal = archivedJournal;
            if (File.Exists(fileJournal))
            {
                OperationSafety.NoLinks(fileJournal);
                var exchange = JsonSerializer.Deserialize<FileExchange>(File.ReadAllText(fileJournal)) ?? throw new InvalidDataException("Datei-Operationszustand fehlt.");
                var previous = target + ".previous-" + id;
                var archivedPrevious = Path.Combine(state.Recovery, "files.before");
                if (exchange.Target != target || exchange.Prepared != state.Prepared || exchange.Previous != previous && exchange.Previous != archivedPrevious) throw new IOException("Ungültiger Datei-Operationszustand.");
                // A crash during archival can leave the tree at either recorded location.
                if (!Directory.Exists(exchange.Previous)) exchange = exchange with { Previous = exchange.Previous == previous ? archivedPrevious : previous };
                RollbackExchange(exchange);
                OperationSafety.Persist(fileJournal, exchange with { Phase = "rolled-back" });
            }
            return Task.CompletedTask;
        });
        await Attempt(async () =>
        {
            if (state.HostPath is not null)
            {
                OperationSafety.NoLinks(state.HostPath);
                var current = File.Exists(state.HostPath) ? OperationSafety.Hash(state.HostPath) : null;
                if (current != state.BeforeHostHash && current != state.AfterHostHash) throw new IOException("Geänderter VHost bleibt erhalten.");
                if (current != state.BeforeHostHash)
                {
                    if (state.BeforeHostHash is null) File.Delete(state.HostPath);
                    else
                    {
                        var backup = Path.Combine(state.Recovery, "vhost.before.json"); OperationSafety.NoLinks(backup);
                        if (OperationSafety.Hash(backup) != state.BeforeHostHash) throw new IOException("VHost-Rückfallkopie: Integritätsfehler.");
                        var host = await store.ReadAsync<VirtualHostDefinition>(backup).ConfigureAwait(false) ?? throw new IOException("VHost-Rückfallkopie fehlt.");
                        await store.WriteAsync(state.HostPath, host).ConfigureAwait(false);
                    }
                }
            }
        });
        await Attempt(async () =>
        {
            var current = (await new ProjectMetadataStore(paths, store).ReadAndPruneAsync().ConfigureAwait(false)).GetValueOrDefault(state.Project);
            if (current != state.BeforeMetadata && current != state.AfterMetadata) throw new IOException("Geänderte Metadaten bleiben erhalten.");
            if (state.BeforeMetadata is null) await new ProjectMetadataStore(paths, store).RemoveWithinOperationAsync(state.Project).ConfigureAwait(false);
            else await new ProjectMetadataStore(paths, store).SetWithinOperationAsync(state.Project, state.BeforeMetadata).ConfigureAwait(false);
            if (state.Ownership is not null)
                OperationSafety.Persist(state.OwnershipPath!, state.Ownership with { RootIdentity = OperationSafety.DirectoryIdentity(target), DatabaseIdentity = state.Database?.Created ?? state.Ownership.DatabaseIdentity });
        });
        if (errors.Count > 0) throw new IOException(string.Join("; ", errors));
        OperationSafety.Persist(journal, state with { Phase = "rolled-back" });
    }

    private static void RollbackExchange(FileExchange exchange)
    {
        OperationSafety.Checkpoint?.Invoke("rollback");
        OperationSafety.NoLinks(exchange.Target); OperationSafety.NoLinks(exchange.Prepared); OperationSafety.NoLinks(exchange.Previous);
        if (Directory.Exists(exchange.Previous))
        {
            if (OperationSafety.DirectoryIdentity(exchange.Previous) != exchange.PreviousIdentity) throw new IOException("Fremder Rückfallordner bleibt erhalten.");
            if (Directory.Exists(exchange.Target))
            {
                if (OperationSafety.DirectoryIdentity(exchange.Target) != exchange.PreparedIdentity || Directory.Exists(exchange.Prepared)) throw new IOException("Fremdes oder widersprüchliches Ziel bleibt erhalten.");
                OperationSafety.MoveDirectory(exchange.Target, exchange.Prepared);
            }
            OperationSafety.MoveDirectory(exchange.Previous, exchange.Target);
        }
        else if (exchange.HadPrevious)
        {
            if (!Directory.Exists(exchange.Target) || OperationSafety.DirectoryIdentity(exchange.Target) != exchange.PreviousIdentity) throw new IOException("Alter Projektstand fehlt; Rückfallpfad prüfen.");
        }
        else if (Directory.Exists(exchange.Target))
        {
            if (OperationSafety.DirectoryIdentity(exchange.Target) != exchange.PreparedIdentity || Directory.Exists(exchange.Prepared)) throw new IOException("Fremdes Ziel bleibt erhalten.");
            OperationSafety.MoveDirectory(exchange.Target, exchange.Prepared);
        }
    }
}
