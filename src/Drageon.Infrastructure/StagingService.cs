using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed partial class StagingService(
    IPortablePathService paths,
    JsonFileStore store,
    IProjectDiscoveryService projects,
    DatabaseBackupService databases,
    IProjectSnapshotService snapshots,
    IAppLogService logs) : IStagingService
{
    public async Task<StagingResult> CreateAsync(StagingRequest request, DatabaseConnection? connection = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await OperationSafety.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadataStore = new ProjectMetadataStore(paths, store);
            var sourceRelative = metadataStore.NormalizeProjectPath(request.SourceProjectPath);
            var source = paths.Resolve(sourceRelative.Split('/'));
            OperationSafety.AssertNoPending(source);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Quellprojekt fehlt: {sourceRelative}");
            var targetRelative = NormalizeNewProjectPath(request.TargetProjectPath);
            var target = paths.Resolve(targetRelative.Split('/'));
            OperationSafety.NoLinks(target);
            OperationSafety.AssertNoPending(target);
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException($"Staging-Ziel existiert bereits: {targetRelative}");
            var sourceMetadata = await projects.GetMetadataAsync(sourceRelative, cancellationToken).ConfigureAwait(false) ?? new ProjectMetadata();
            if (request.CopyDatabase && string.IsNullOrWhiteSpace(sourceMetadata.Database)) throw new InvalidOperationException("Dem Quellprojekt ist keine Datenbank zugeordnet.");
            if (request.CopyDatabase && string.IsNullOrWhiteSpace(request.TargetDatabase)) throw new InvalidOperationException("Für die Staging-Kopie fehlt ein Datenbankname.");
            if (request.CopyDatabase && (await databases.GetDatabasesAsync(connection, cancellationToken).ConfigureAwait(false)).Contains(request.TargetDatabase!, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Die Datenbank {request.TargetDatabase} existiert bereits.");
            if (request.CreateVirtualHost)
            {
                var host = GetHost(request.Url);
                new VirtualHostStore(paths, store).Resolve(new(host, targetRelative));
                if ((await new VirtualHostStore(paths, store).ReadAsync(cancellationToken).ConfigureAwait(false)).Any(item => item.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
                    || File.Exists(paths.Resolve("app", "config", "vhosts", VHostFileName(host)))) throw new IOException($"Virtual Host {host} existiert bereits.");
            }

            var temporary = paths.Resolve("app", "var", "tmp", $"staging-files-{Guid.NewGuid():N}");
            string? createdVhostPath = null; var databaseCreated = false; var targetCreated = false;
            string? createdVhostHash = null;
            var identity = Guid.NewGuid().ToString("N");
            var ownershipPath = paths.Resolve("app", "config", "staging", identity + ".json");
            string? rootIdentity = null;
            var creationJournal = target + ".create-operation-" + identity + ".json";
            var server = request.CopyDatabase ? await databases.GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false) : null;
            var serverIdentity = server is null ? null : await databases.GetInstanceIdentityAsync(server, cancellationToken).ConfigureAwait(false);
            if (server is not null) await databases.GetDatabaseStorageIdentityAsync(server, request.TargetDatabase!, cancellationToken, false).ConfigureAwait(false);
            DatabaseStorageIdentity? databaseIdentity = null;
            OperationSafety.Persist(creationJournal, new { Project = targetRelative, Ownership = ownershipPath, Database = request.TargetDatabase, Server = serverIdentity, Phase = "prepared" });
            try
            {
                progress?.Report("files");
                var entries = await Task.Run(() => ArchiveFileTree.Capture(source, temporary, cancellationToken, progress, compress: false), cancellationToken).ConfigureAwait(false);
                var archiveManifest = new ProjectSnapshotManifest { FormatVersion = 3, Snapshot = new("", sourceRelative, "", DateTimeOffset.Now, "", null, null, null), Entries = [] };
                var prepared = await Task.Run(() => ArchiveFileTree.Prepare(temporary, target, archiveManifest, entries, logs, cancellationToken, progress), cancellationToken).ConfigureAwait(false);
                SafeFileTree.Exchange(prepared, target, logs, cancellationToken, requireAbsent: true);
                targetCreated = true;
                rootIdentity = OperationSafety.DirectoryIdentity(target);
                OperationSafety.Persist(creationJournal, new { Project = targetRelative, Ownership = ownershipPath, Database = request.TargetDatabase, Server = serverIdentity, Phase = "files-created" });
                if (request.CopyDatabase)
                {
                    progress?.Report("database");
                    databaseIdentity = await databases.CreateOwnedDatabaseFromAsync(sourceMetadata.Database!, request.TargetDatabase!, connection, cancellationToken).ConfigureAwait(false);
                    databaseCreated = true;
                    OperationSafety.Persist(ownershipPath, new StagingOwnership(identity, targetRelative, null, null, request.TargetDatabase, serverIdentity, rootIdentity, databaseIdentity));
                    OperationSafety.Persist(creationJournal, new { Project = targetRelative, Ownership = ownershipPath, Database = request.TargetDatabase, Server = serverIdentity, Phase = "database-created" });
                }

                string? vhostName = null;
                if (request.CreateVirtualHost)
                {
                    progress?.Report("vhost");
                    vhostName = GetHost(request.Url);
                    var sourceVhost = await FindVirtualHostAsync(sourceMetadata.Vhost, cancellationToken).ConfigureAwait(false);
                    var documentRoot = targetRelative;
                    if (sourceVhost is not null)
                    {
                        var resolvedSourceRoot = ResolveDocumentRoot(sourceVhost.DocumentRoot);
                        var suffix = Path.GetRelativePath(source, resolvedSourceRoot);
                        if (suffix == ".")
                            documentRoot = targetRelative;
                        else if (!Path.IsPathRooted(suffix) && suffix != ".." && !suffix.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                            documentRoot = Path.Combine(targetRelative, suffix).Replace('\\', '/');
                    }
                    var newVhost = sourceVhost is null
                        ? new VirtualHostDefinition(vhostName, documentRoot)
                        : sourceVhost with { Host = vhostName, DocumentRoot = documentRoot };
                    var candidate = Path.Combine(paths.Resolve("app", "config", "vhosts"), VHostFileName(vhostName));
                    OperationSafety.NoLinks(candidate);
                    using (var stream = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        createdVhostPath = candidate;
                        System.Text.Json.JsonSerializer.Serialize(stream, newVhost, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                        stream.Flush(true);
                    }
                    createdVhostHash = OperationSafety.Hash(candidate);
                    OperationSafety.Persist(ownershipPath, new StagingOwnership(identity, targetRelative, candidate, createdVhostHash, databaseCreated ? request.TargetDatabase : null, serverIdentity, rootIdentity, databaseIdentity));
                    OperationSafety.Checkpoint?.Invoke("staging-vhost-created");
                }

                OperationSafety.Persist(ownershipPath, new StagingOwnership(identity, targetRelative, createdVhostPath, createdVhostPath is null ? null : OperationSafety.Hash(createdVhostPath), databaseCreated ? request.TargetDatabase : null, serverIdentity, rootIdentity, databaseIdentity));
                progress?.Report("metadata");
                await metadataStore.SetWithinOperationAsync(targetRelative, new ProjectMetadata
                {
                    Name = request.ProjectName.Trim(),
                    Database = request.CopyDatabase ? request.TargetDatabase : null,
                    Url = string.IsNullOrWhiteSpace(request.Url) ? null : request.Url.Trim(),
                    Vhost = vhostName,
                    StagingOf = sourceRelative,
                    StagingIdentity = identity
                }, cancellationToken).ConfigureAwait(false);
                OperationSafety.Persist(creationJournal, new { Project = targetRelative, Ownership = ownershipPath, Database = request.TargetDatabase, Server = serverIdentity, Phase = "completed" });
                try { File.Delete(creationJournal); }
                catch (Exception cleanup) { logs.Write("Staging", $"Erstellung abgeschlossen; Operationsdatei erhalten: {creationJournal}; {cleanup.Message}", LogSeverity.Warning); }
                logs.Write("Staging", $"Staging erstellt: {targetRelative}");
                return new(targetRelative, request.CopyDatabase ? request.TargetDatabase : null, request.Url);
            }
            catch (Exception original)
            {
                var cleanupErrors = new List<string>();
                try
                {
                    if (targetCreated && Directory.Exists(target))
                    {
                        if (rootIdentity is null || OperationSafety.DirectoryIdentity(target) != rootIdentity) throw new IOException("Fremder Ersatzordner bleibt erhalten.");
                        SafeFileTree.Delete(target);
                    }
                }
                catch (Exception cleanup) { cleanupErrors.Add($"Projekt {target}: {cleanup.Message}"); }
                if (createdVhostPath is not null)
                    try
                    {
                        OperationSafety.NoLinks(createdVhostPath);
                        if (createdVhostHash is null || (File.Exists(createdVhostPath) && OperationSafety.Hash(createdVhostPath) != createdVhostHash)) throw new IOException("VHost wurde geändert oder Erzeugungsnachweis ist unvollständig; Ressource bleibt erhalten.");
                        File.Delete(createdVhostPath);
                    }
                    catch (Exception cleanup) { cleanupErrors.Add($"VHost {createdVhostPath}: {cleanup.Message}"); }
                // A server may have been switched/restarted after copying. Never resolve the active
                // connection anew and drop a same-named database. Preserve it for explicit review.
                if (databaseCreated) cleanupErrors.Add($"Erzeugte Datenbank erhalten: {request.TargetDatabase}; Server {serverIdentity}; Nachweis {ownershipPath}");
            var manualRecovery = cleanupErrors.Count > 0 || original is RecoveryOperationException { Outcome: RecoveryOutcome.ManualRecoveryRequired };
            try { OperationSafety.Persist(creationJournal, new { Project = targetRelative, Ownership = ownershipPath, Database = request.TargetDatabase, Server = serverIdentity, Phase = manualRecovery ? "manual-recovery" : "rolled-back" }); }
                catch (Exception journalFailure) { cleanupErrors.Add($"Operationsjournal {creationJournal}: {journalFailure.Message}"); }
                if (cleanupErrors.Count > 0) throw new RecoveryOperationException(RecoveryOutcome.ManualRecoveryRequired, $"{original.Message}; Manuelle Wiederherstellung erforderlich: {string.Join("; ", cleanupErrors)}; Zustand {creationJournal}", original);
                throw;
            }
            finally { try { if (Directory.Exists(temporary)) SafeFileTree.Delete(temporary); } catch (Exception cleanup) { logs.Write("Staging", $"Temporäre Dateien erhalten: {temporary}; {cleanup.Message}", LogSeverity.Warning); } }
        }
        finally { OperationSafety.Gate.Release(); }
    }

    public async Task DeleteAsync(string stagingProjectPath, bool deleteDatabase, DatabaseConnection? connection = null, CancellationToken cancellationToken = default, string? confirmedPlan = null)
    {
        await OperationSafety.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadataStore = new ProjectMetadataStore(paths, store);
            var relative = metadataStore.NormalizeProjectPath(stagingProjectPath);
            if (confirmedPlan is not null) ProjectSnapshotService.CheckPlan(confirmedPlan, await GetDeletePlanAsync(relative, deleteDatabase, connection, cancellationToken).ConfigureAwait(false));
            var metadata = await projects.GetMetadataAsync(relative, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Für dieses Projekt sind keine Staging-Metadaten vorhanden.");
            if (string.IsNullOrWhiteSpace(metadata.StagingOf)) throw new InvalidOperationException("Das ausgewählte Projekt ist kein von Drageon erzeugtes Staging-Projekt.");
            var target = paths.Resolve(relative.Split('/'));
            OperationSafety.NoLinks(target);
            var ownershipFile = metadata.StagingIdentity is null ? null : paths.Resolve("app", "config", "staging", OperationSafety.Relative(metadata.StagingIdentity) + ".json");
            if (ownershipFile is not null) OperationSafety.NoLinks(ownershipFile);
            var ownership = ownershipFile is null ? null : await store.ReadAsync<StagingOwnership>(ownershipFile, cancellationToken).ConfigureAwait(false);
            if (ownership is null || ownership.Identity != metadata.StagingIdentity || ownership.ProjectPath != relative || ownership.RootIdentity is null || ownership.RootIdentity != OperationSafety.DirectoryIdentity(target))
                throw new InvalidOperationException("Unverändert abgebrochen: Staging-Erzeugungsnachweis fehlt oder der Projektordner wurde ersetzt. Unklare Ressourcen bleiben erhalten.");
            if (deleteDatabase && metadata.Database is not null && (ownership.CreatedDatabase != metadata.Database || ownership.DatabaseIdentity is null || ownership.ServerIdentity is null))
                throw new InvalidOperationException("Unverändert abgebrochen: Datenbank wurde neu zugeordnet oder ihr dauerhafter Erzeugungsnachweis fehlt. Fremde und ältere unklare Bestände bleiben erhalten.");
            var all = await metadataStore.ReadAndPruneAsync(cancellationToken).ConfigureAwait(false);
            if (deleteDatabase && metadata.Database is not null && all.Any(pair => !pair.Key.Equals(relative, StringComparison.OrdinalIgnoreCase) && pair.Value.Database?.Equals(metadata.Database, StringComparison.OrdinalIgnoreCase) == true))
                throw new InvalidOperationException("Unverändert abgebrochen: Staging-Datenbank wird von einem weiteren Projekt verwendet.");
            using var databaseOperation = deleteDatabase && metadata.Database is not null
                ? await databases.AcquireOwnedDatabaseAsync(metadata.Database, ownership.ServerIdentity!, ownership.DatabaseIdentity!, connection, cancellationToken).ConfigureAwait(false) : null;
            string? ownedVhost = null;
            if (metadata.Vhost is not null)
            {
                var candidate = paths.Resolve("app", "config", "vhosts", VHostFileName(metadata.Vhost));
                OperationSafety.NoLinks(candidate);
                var host = await store.ReadAsync<VirtualHostDefinition>(candidate, cancellationToken).ConfigureAwait(false);
                if (ownership?.ProjectPath == relative && ownership.Identity == metadata.StagingIdentity && ownership.VhostPath == candidate
                    && File.Exists(candidate) && OperationSafety.Hash(candidate) == ownership.VhostHash
                    && host?.Host == metadata.Vhost && OperationSafety.Contains(target, ResolveDocumentRoot(host.DocumentRoot))
                    && !all.Any(pair => !pair.Key.Equals(relative, StringComparison.OrdinalIgnoreCase) && pair.Value.Vhost?.Equals(metadata.Vhost, StringComparison.OrdinalIgnoreCase) == true))
                    ownedVhost = candidate;
                else throw new InvalidOperationException($"Unverändert abgebrochen: VHost {metadata.Vhost} ist fremd, umgewidmet, gemeinsam verwendet oder ohne Erzeugungsnachweis. Ressource bleibt erhalten.");
            }
        var referencedHosts = await new VirtualHostStore(paths, store).ReadAsync(cancellationToken).ConfigureAwait(false);
        if (referencedHosts.Any(host => !host.Host.Equals(metadata.Vhost, StringComparison.OrdinalIgnoreCase) && OperationSafety.Contains(target, host.DocumentRoot)))
            throw new InvalidOperationException("Unverändert abgebrochen: Ein weiterer VHost verwendet den Staging-Projektordner. Projekt und Ressourcen bleiben erhalten.");
            var snapshotList = await snapshots.GetProjectSnapshotsAsync(relative, cancellationToken).ConfigureAwait(false);
            if (snapshotList.Count > 0 && snapshots is not ProjectSnapshotService) throw new InvalidOperationException("Unverändert abgebrochen: Snapshot-Service unterstützt keine reversible Bereinigung.");
            var id = Guid.NewGuid().ToString("N");
            var quarantine = target + ".delete-" + id;
            var recovery = target + ".recovery-" + id;
            var journal = target + ".delete-operation-" + id + ".json";
            OperationSafety.AssertNoPending(target);
            OperationSafety.CreateDirectoryExclusive(recovery);
            var state = new StagingDeletion(target, quarantine, recovery, ownedVhost, snapshotList, "preparing", OperationSafety.DirectoryIdentity(recovery),
                databaseOperation is null ? null : metadata.Database, databaseOperation is null ? null : ownership.ServerIdentity,
                databaseOperation is null ? null : ownership.DatabaseIdentity);
            OperationSafety.Persist(Path.Combine(recovery, "metadata.json"), metadata);
            OperationSafety.Persist(journal, state);
            try
            {
                await Task.Run(() => ArchiveFileTree.Capture(target, recovery, cancellationToken), cancellationToken).ConfigureAwait(false);
                var fileManifest = new ProjectSnapshotManifest
                {
                    FormatVersion = 3, Entries = [],
                    Snapshot = new("", relative, metadata.Name ?? Path.GetFileName(relative), DateTimeOffset.Now, "Staging recovery", null, null, null),
                    ArchiveSize = new FileInfo(Path.Combine(recovery, "files.zip")).Length,
                    ArchiveSha256 = OperationSafety.Hash(Path.Combine(recovery, "files.zip")),
                    IndexSha256 = OperationSafety.Hash(Path.Combine(recovery, "files.index.json"))
                };
                await store.WriteAsync(Path.Combine(recovery, "files.manifest.json"), fileManifest, cancellationToken).ConfigureAwait(false);
                if (ownedVhost is not null) File.Copy(ownedVhost, Path.Combine(recovery, "vhost.json"));
                (string CharacterSet, string? Collation)? schema = databaseOperation is null ? null : await databaseOperation.BackupAsync(Path.Combine(recovery, "database.sql"), cancellationToken).ConfigureAwait(false);
                OperationSafety.Checkpoint?.Invoke("staging-delete-prepared");
                if (OperationSafety.DirectoryIdentity(target) != ownership.RootIdentity) throw new IOException("Zwischenzeitlich angelegter fremder Projektordner bleibt erhalten.");
                cancellationToken.ThrowIfCancellationRequested();
                state = state with { Phase = "prepared", DatabaseSha256 = databaseOperation is null ? null : OperationSafety.Hash(Path.Combine(recovery, "database.sql")), CharacterSet = schema?.CharacterSet, Collation = schema?.Collation };
                OperationSafety.Persist(journal, state);
            }
            catch (Exception original)
            {
                try { OperationSafety.Persist(journal, state with { Phase = "rolled-back" }); }
                catch (Exception failure) { throw new RecoveryOperationException(RecoveryOutcome.ManualRecoveryRequired, $"{original.Message}; Zustand {journal}; Sicherung {recovery}; Journalfehler: {failure.Message}", original); }
                throw new RecoveryOperationException(RecoveryOutcome.Unchanged, $"Unverändert abgebrochen: {original.Message}; Zustand {journal}; Vorbereitung {recovery}", original);
            }
            var moved = false;
            try
            {
                OperationSafety.MoveDirectory(target, quarantine, cancellationToken);
                moved = true;
                if (OperationSafety.DirectoryIdentity(quarantine) != ownership.RootIdentity) throw new IOException("Fremder Ersatzordner wurde verschoben und bleibt für die manuelle Rückkehr erhalten.");
                OperationSafety.Persist(journal, state with { Phase = "project-moved" });
                OperationSafety.Checkpoint?.Invoke("staging-project-moved");
                SafeFileTree.Delete(quarantine);
                OperationSafety.Persist(journal, state with { Phase = "project-removed" });
                OperationSafety.Checkpoint?.Invoke("staging-project-removed");
                if (ownedVhost is not null)
                {
                    if (OperationSafety.Hash(ownedVhost) != ownership!.VhostHash) throw new IOException("VHost wurde zwischenzeitlich geändert und bleibt erhalten.");
                    File.Delete(ownedVhost);
                }
                OperationSafety.Persist(journal, state with { Phase = "resources-removed" });
                OperationSafety.Checkpoint?.Invoke("staging-resources-removed");
                await metadataStore.RemoveWithinOperationAsync(relative, CancellationToken.None).ConfigureAwait(false);
                if (databaseOperation is not null)
                {
                    OperationSafety.Persist(journal, state with { Phase = "database-before-drop" });
                    await databaseOperation.DropAsync(cancellationToken).ConfigureAwait(false);
                    OperationSafety.Persist(journal, state with { Phase = "database-removed" });
                    OperationSafety.Checkpoint?.Invoke("staging-database-removed");
                }
                foreach (var snapshot in snapshotList)
                {
                    if (snapshots is not ProjectSnapshotService concrete) throw new InvalidOperationException("Snapshot-Service unterstützt keine reversible Bereinigung. Sicherungen bleiben erhalten.");
                    await concrete.QuarantineSnapshotCoreAsync(snapshot.DirectoryPath, relative, recovery).ConfigureAwait(false);
                    OperationSafety.Checkpoint?.Invoke("staging-snapshot-moved");
                }
                OperationSafety.Persist(journal, state with { Phase = "completed" });
            }
            catch (Exception original)
            {
                if (!moved)
                {
                    try { OperationSafety.Persist(journal, state with { Phase = "rolled-back" }); }
                    catch (Exception journalFailure) { throw new RecoveryOperationException(RecoveryOutcome.ManualRecoveryRequired, $"{original.Message}; Journal {journal}: {journalFailure.Message}; Sicherung {recovery}", original); }
                    throw new RecoveryOperationException(RecoveryOutcome.Unchanged, $"Unverändert abgebrochen: {original.Message}. Sicherungen erhalten: {recovery}", original);
                }
                throw new RecoveryOperationException(RecoveryOutcome.ManualRecoveryRequired, $"Manuelle Wiederherstellung erforderlich: Dateien, VHost und Metadaten in {recovery}{(state.Database is null ? "" : "; Datenbankdump: " + Path.Combine(recovery, "database.sql"))}; Zustand {journal}; Restbestand {quarantine}. {original.Message}", original);
            }
            // Destruction is committed. Recovery data is needed until this point, never afterwards.
            try
            {
                OperationSafety.Checkpoint?.Invoke("staging-cleanup");
                if (OperationSafety.DirectoryIdentity(recovery) != state.RecoveryIdentity) throw new IOException($"Fremder Ersatzordner bleibt erhalten: {recovery}");
                SafeFileTree.Delete(recovery);
                if (snapshots is ProjectSnapshotService snapshotService)
                    foreach (var snapshot in snapshotList) snapshotService.RemoveSnapshotReceipt(paths.Resolve(snapshot.DirectoryPath.Split('/')), relative);
                RemoveFinishedProjectOperations(target, relative, ownershipFile!, ownership.Identity);
                OperationSafety.NoLinks(ownershipFile!);
                File.Delete(ownershipFile!);
                File.Delete(journal);
                logs.Write("Staging", $"Staging vollständig gelöscht: {relative}; temporäre Rückfallkopien und Snapshots entfernt. Datenbank gelöscht: {state.Database ?? "keine"}.");
            }
            catch (Exception cleanup)
            {
                throw new RecoveryOperationException(RecoveryOutcome.Completed, $"Staging gelöscht; Bereinigung der Rückstände fehlgeschlagen: {recovery}; Zustand: {journal}. {cleanup.Message}", cleanup);
            }
        }
        finally { OperationSafety.Gate.Release(); }
    }
    private void RemoveFinishedProjectOperations(string target, string relative, string ownershipFile, string stagingIdentity)
    {
        var recoveryRoot = paths.Resolve("data", "recovery"); OperationSafety.NoLinks(recoveryRoot);
        if (Directory.Exists(recoveryRoot))
            foreach (var recovery in Directory.EnumerateDirectories(recoveryRoot, "restore-*"))
            {
                OperationSafety.NoLinks(recovery);
                var id = Path.GetFileName(recovery)["restore-".Length..];
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                var archivedJournal = Path.Combine(recovery, "restore.operation.json"); OperationSafety.NoLinks(archivedJournal);
                if (!File.Exists(archivedJournal)) continue;
                var restore = System.Text.Json.JsonSerializer.Deserialize<ProjectRestoreState>(File.ReadAllText(archivedJournal));
                if (restore?.Project != relative || restore.Phase is not ("completed" or "rolled-back")
                    || restore.BeforeMetadata?.StagingIdentity != stagingIdentity || restore.AfterMetadata?.StagingIdentity != stagingIdentity
                    || restore.Prepared != target + ".prepare-" + id || restore.Recovery != recovery) continue;
                if (restore.RecoveryIdentity != OperationSafety.DirectoryIdentity(recovery)) throw new IOException($"Fremder Restore-Rückfallordner bleibt erhalten: {recovery}");
                var previous = Path.Combine(recovery, "files.before"); OperationSafety.NoLinks(previous);
                if (Directory.Exists(previous))
                {
                    var exchangeFile = Path.Combine(recovery, "files.operation.json"); OperationSafety.NoLinks(exchangeFile);
                    var exchange = System.Text.Json.JsonSerializer.Deserialize<FileExchange>(File.ReadAllText(exchangeFile));
                    if (exchange?.Target != target || exchange.Previous != previous || exchange.Prepared != restore.Prepared
                        || exchange.PreviousIdentity != OperationSafety.DirectoryIdentity(previous)) throw new IOException($"Fremder Datei-Rückfallordner bleibt erhalten: {previous}");
                }
                SafeFileTree.Delete(recovery);
            }
        foreach (var journal in Directory.EnumerateFiles(Path.GetDirectoryName(target)!, Path.GetFileName(target) + ".*operation-*.json"))
        {
            OperationSafety.NoLinks(journal);
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(journal));
            var state = document.RootElement;
            if (!state.TryGetProperty("Phase", out var phase) || phase.GetString() is not ("completed" or "rolled-back")) continue;
            var creationPrefix = target + ".create-operation-";
            if (journal.StartsWith(creationPrefix, StringComparison.Ordinal) && Guid.TryParseExact(journal[creationPrefix.Length..^5], "N", out _)
                && state.TryGetProperty("Project", out var project) && project.GetString() == relative
                && state.TryGetProperty("Ownership", out var ownership) && ownership.GetString() == ownershipFile)
            {
                File.Delete(journal);
                continue;
            }
            var deletionPrefix = target + ".delete-operation-";
            if (journal.StartsWith(deletionPrefix, StringComparison.Ordinal) && Guid.TryParseExact(journal[deletionPrefix.Length..^5], "N", out _))
            {
                var deletion = System.Text.Json.JsonSerializer.Deserialize<StagingDeletion>(state.GetRawText());
                var deletionSuffix = journal[deletionPrefix.Length..^5];
                if (deletion is null || deletion.Target != target || deletion.Quarantine != target + ".delete-" + deletionSuffix || deletion.Recovery != target + ".recovery-" + deletionSuffix) continue;
                OperationSafety.NoLinks(deletion.Recovery);
                if (Directory.Exists(deletion.Quarantine) || File.Exists(deletion.Quarantine)) throw new IOException($"Unklarer Restbestand bleibt erhalten: {deletion.Quarantine}");
                if (Directory.Exists(deletion.Recovery))
                {
                    if (deletion.RecoveryIdentity is null || OperationSafety.DirectoryIdentity(deletion.Recovery) != deletion.RecoveryIdentity) throw new IOException($"Unklarer oder fremder Rückfallordner bleibt erhalten: {deletion.Recovery}");
                    var savedMetadataPath = Path.Combine(deletion.Recovery, "metadata.json");
                    OperationSafety.NoLinks(savedMetadataPath);
                    var savedMetadata = System.Text.Json.JsonSerializer.Deserialize<ProjectMetadata>(File.ReadAllText(savedMetadataPath));
                    if (savedMetadata?.StagingIdentity != stagingIdentity) throw new IOException($"Unklarer Rückfallordner bleibt erhalten: {deletion.Recovery}");
                    SafeFileTree.Delete(deletion.Recovery);
                }
                File.Delete(journal);
                continue;
            }
            var exchangePrefix = target + ".operation-";
            var restorePrefix = target + ".restore-operation-";
            if (journal.StartsWith(restorePrefix, StringComparison.Ordinal) && Guid.TryParseExact(journal[restorePrefix.Length..^5], "N", out _))
            {
                var restore = System.Text.Json.JsonSerializer.Deserialize<ProjectRestoreState>(state.GetRawText());
                var restoreId = journal[restorePrefix.Length..^5];
                if (restore?.Project != relative || restore.BeforeMetadata?.StagingIdentity != stagingIdentity || restore.AfterMetadata?.StagingIdentity != stagingIdentity
                    || restore.Prepared != target + ".prepare-" + restoreId || restore.Recovery != paths.Resolve("data", "recovery", "restore-" + restoreId)) continue;
                OperationSafety.NoLinks(restore.Recovery);
                if (Directory.Exists(restore.Recovery))
                {
                    if (restore.RecoveryIdentity is null || OperationSafety.DirectoryIdentity(restore.Recovery) != restore.RecoveryIdentity) throw new IOException($"Fremder Restore-Rückfallordner bleibt erhalten: {restore.Recovery}");
                    SafeFileTree.Delete(restore.Recovery);
                }
                File.Delete(journal);
                continue;
            }
            if (!journal.StartsWith(exchangePrefix, StringComparison.Ordinal) || !Guid.TryParseExact(journal[exchangePrefix.Length..^5], "N", out _)) continue;
            var exchange = System.Text.Json.JsonSerializer.Deserialize<FileExchange>(state.GetRawText());
            var suffix = journal[exchangePrefix.Length..^5];
            if (exchange is null || exchange.Target != target || exchange.Prepared != target + ".prepare-" + suffix
                || exchange.Previous != target + ".previous-" + suffix && exchange.Previous != paths.Resolve("data", "recovery", "restore-" + suffix, "files.before")) continue;
            if (Directory.Exists(exchange.Previous))
            {
                if (exchange.PreviousIdentity is null || OperationSafety.DirectoryIdentity(exchange.Previous) != exchange.PreviousIdentity) throw new IOException($"Fremder Rückfallordner bleibt erhalten: {exchange.Previous}");
                SafeFileTree.Delete(exchange.Previous);
            }
            if (Directory.Exists(exchange.Prepared))
            {
                if (exchange.PreparedIdentity is null || OperationSafety.DirectoryIdentity(exchange.Prepared) != exchange.PreparedIdentity) throw new IOException($"Fremder Vorbereitungsordner bleibt erhalten: {exchange.Prepared}");
                SafeFileTree.Delete(exchange.Prepared);
            }
            File.Delete(journal);
        }
    }
    private string NormalizeNewProjectPath(string value)
    {
        var candidate = value.Replace('\\', '/').Trim('/');
        if (!candidate.StartsWith("www/", StringComparison.OrdinalIgnoreCase)) candidate = $"www/{candidate}";
        var parts = candidate.Split('/');
        if (parts.Length != 2 || parts[1].Length == 0 || parts[1] is "." or ".." || parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Staging-Pfad muss direkt unter www/ liegen.");
        OperationSafety.Relative(candidate);
        if (OperationSafety.IsOperationDirectory(candidate)) throw new ArgumentException("Reservierter Operationspfad.");
        return $"www/{parts[1]}";
    }
    private async Task<VirtualHostDefinition?> FindVirtualHostAsync(string? hostName, CancellationToken cancellationToken)
    {
        if (hostName is null) return null;
        foreach (var file in Directory.EnumerateFiles(paths.Resolve("app", "config", "vhosts"), "*.json"))
        {
            var host = await store.ReadAsync<VirtualHostDefinition>(file, cancellationToken).ConfigureAwait(false);
            if (host?.Host.Equals(hostName, StringComparison.OrdinalIgnoreCase) == true) return host;
        }
        return null;
    }
    private string ResolveDocumentRoot(string root) => Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(paths.AppRoot, root.Replace('/', Path.DirectorySeparatorChar)));
    private static string GetHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host) ? uri.Host : throw new ArgumentException("Ungültige Staging-URL.");
    private static string VHostFileName(string host) { var invalid = Path.GetInvalidFileNameChars(); return new string(host.Trim().ToLowerInvariant().Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()) + ".json"; }
}
