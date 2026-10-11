using Drageon.Core;
using System.Text.Json;

namespace Drageon.Infrastructure;

public sealed partial class StagingService
{
    public async Task RecoverDeletionAsync(string journalPath, string expectedProjectPath, CancellationToken cancellationToken = default)
    {
        await OperationSafety.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadataStore = new ProjectMetadataStore(paths, store);
            var relative = metadataStore.NormalizeProjectPath(expectedProjectPath);
            var target = paths.Resolve(relative.Split('/'));
            var journal = Path.GetFullPath(journalPath);
            OperationSafety.NoLinks(journal);
            var prefix = target + ".delete-operation-";
            if (!journal.StartsWith(prefix, StringComparison.Ordinal) || !journal.EndsWith(".json", StringComparison.Ordinal)
                || !Guid.TryParseExact(journal[prefix.Length..^5], "N", out _)) throw new InvalidDataException("Ungültiges Staging-Journal.");
            var suffix = journal[prefix.Length..^5];
            var state = JsonSerializer.Deserialize<StagingDeletion>(File.ReadAllText(journal)) ?? throw new InvalidDataException("Operationszustand fehlt.");
            if (state.Target != target || state.Quarantine != target + ".delete-" + suffix || state.Recovery != target + ".recovery-" + suffix) throw new InvalidDataException("Manipulierter Operationszustand.");
            if (Directory.Exists(target) || File.Exists(target)) throw new IOException("Unverändert abgebrochen: Wiederherstellungsziel existiert bereits und bleibt erhalten.");
            OperationSafety.NoLinks(state.Recovery);
            if (state.RecoveryIdentity is not null && OperationSafety.DirectoryIdentity(state.Recovery) != state.RecoveryIdentity) throw new IOException("Unverändert abgebrochen: Rückfallordner wurde ersetzt und bleibt erhalten.");
            OperationSafety.NoLinks(Path.Combine(state.Recovery, "metadata.json"));
            var fileManifestPath = Path.Combine(state.Recovery, "files.manifest.json"); OperationSafety.NoLinks(fileManifestPath);
            ProjectSnapshotManifest fileManifest;
            if (File.Exists(fileManifestPath))
            {
                fileManifest = await store.ReadAsync<ProjectSnapshotManifest>(fileManifestPath, cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("ZIP-Rückfallmanifest fehlt.");
                if (fileManifest.FormatVersion != 3 || fileManifest.Snapshot.ProjectPath != relative) throw new InvalidDataException("Fremder ZIP-Rückfallzustand.");
            }
            else
            {
                var legacyIndex = Path.Combine(state.Recovery, "recovery-entries.json"); OperationSafety.NoLinks(legacyIndex);
                var legacyEntries = JsonSerializer.Deserialize<IReadOnlyList<SnapshotFileEntry>>(File.ReadAllText(legacyIndex)) ?? throw new InvalidDataException("Rückfallmanifest fehlt.");
                fileManifest = new() { FormatVersion = 2, Entries = legacyEntries, Snapshot = new("", relative, "", DateTimeOffset.Now, "", null, null, null) };
            }
            var entries = await ArchiveFileTree.ReadEntries(state.Recovery, fileManifest, cancellationToken).ConfigureAwait(false);
            await Task.Run(() => ArchiveFileTree.Validate(state.Recovery, fileManifest, entries), cancellationToken).ConfigureAwait(false);
            var metadata = JsonSerializer.Deserialize<ProjectMetadata>(File.ReadAllText(Path.Combine(state.Recovery, "metadata.json"))) ?? throw new InvalidDataException("Rückfallmetadaten fehlen.");
            var all = await metadataStore.ReadAndPruneAsync(cancellationToken).ConfigureAwait(false);
            if (all.TryGetValue(relative, out var current) && current != metadata) throw new IOException("Unverändert abgebrochen: Projektmetadaten wurden geändert.");
            var sqlBackup = Path.Combine(state.Recovery, "database.sql");
            string? databaseOwnershipPath = null;
            StagingOwnership? databaseOwnership = null;
            if (state.Database is not null)
            {
                if (state.Database != metadata.Database || state.ServerIdentity is null || state.DatabaseIdentity is null || state.CharacterSet is null
                    || all.Any(pair => pair.Key != relative && pair.Value.Database?.Equals(state.Database, StringComparison.OrdinalIgnoreCase) == true))
                    throw new InvalidDataException("Datenbank-Rückfallzustand ist unklar oder wird von einem anderen Projekt verwendet.");
                OperationSafety.NoLinks(sqlBackup);
                if (state.DatabaseSha256 is null || OperationSafety.Hash(sqlBackup) != state.DatabaseSha256) throw new InvalidDataException("SQL-Rückfalldump fehlt oder ist beschädigt.");
                if (state.Phase == "database-restoring") throw new IOException($"Manuelle Datenbankwiederherstellung erforderlich: unterbrochener SQL-Import; {sqlBackup}; {journal}");
                if (metadata.StagingIdentity is null) throw new InvalidDataException("Staging-Datenbanknachweis fehlt.");
                databaseOwnershipPath = paths.Resolve("app", "config", "staging", OperationSafety.Relative(metadata.StagingIdentity) + ".json");
                OperationSafety.NoLinks(databaseOwnershipPath);
                databaseOwnership = await store.ReadAsync<StagingOwnership>(databaseOwnershipPath, cancellationToken).ConfigureAwait(false);
                if (databaseOwnership is null || databaseOwnership.Identity != metadata.StagingIdentity || databaseOwnership.CreatedDatabase != state.Database || databaseOwnership.ProjectPath != relative
                    || databaseOwnership.ServerIdentity != state.ServerIdentity || databaseOwnership.DatabaseIdentity != state.DatabaseIdentity) throw new InvalidDataException("Fremder oder geänderter Staging-Datenbanknachweis.");
            }
            using var databaseOperation = state.Database is null ? null : await databases.AcquireOwnedDatabaseAsync(state.Database, state.ServerIdentity!, state.DatabaseIdentity!, null, cancellationToken, allowMissing: true).ConfigureAwait(false);
            var vhostBackup = Path.Combine(state.Recovery, "vhost.json");
            if (state.Vhost is not null)
            {
                OperationSafety.NoLinks(state.Vhost); OperationSafety.NoLinks(vhostBackup);
                if (metadata.Vhost is null || state.Vhost != paths.Resolve("app", "config", "vhosts", VHostFileName(metadata.Vhost))) throw new InvalidDataException("Fremder VHost im Rückfallzustand.");
                if (File.Exists(state.Vhost) && OperationSafety.Hash(state.Vhost) != OperationSafety.Hash(vhostBackup)) throw new IOException("Unverändert abgebrochen: Fremder oder geänderter VHost bleibt erhalten.");
                if (all.Any(pair => pair.Key != relative && pair.Value.Vhost?.Equals(metadata.Vhost, StringComparison.OrdinalIgnoreCase) == true)) throw new IOException("Unverändert abgebrochen: VHost wird von einem anderen Projekt verwendet.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            var snapshotMoves = new List<(string Source, string Target)>();
            foreach (var snapshot in state.Snapshots)
            {
                // Original addresses are derived from this project and the saved snapshot basename.
                var stamp = OperationSafety.Relative(Path.GetFileName(snapshot.DirectoryPath.Replace('/', Path.DirectorySeparatorChar)));
                var source = Path.Combine(state.Recovery, "snapshots", stamp);
                if (!Directory.Exists(source)) continue;
                OperationSafety.NoLinks(source);
                var destination = paths.Resolve("data", "snapshots", Path.GetFileName(relative), stamp);
                OperationSafety.NoLinks(destination);
                if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Unverändert abgebrochen: Fremdes Snapshot-Ziel bleibt erhalten.");
                snapshotMoves.Add((source, destination));
            }
            // Prepare links and verify individual contents before any database recovery mutation.
            var prepared = await Task.Run(() => ArchiveFileTree.Prepare(state.Recovery, target, fileManifest, entries, logs, cancellationToken), cancellationToken).ConfigureAwait(false);
            var preparedIdentity = OperationSafety.DirectoryIdentity(prepared);
            try
            {
                if (databaseOperation is not null)
                {
                    var restoredIdentity = await databaseOperation.RestoreAsync(sqlBackup, state.CharacterSet!, state.Collation, identity =>
                    {
                        state = state with { DatabaseIdentity = identity, Phase = "database-restoring" };
                        OperationSafety.Persist(journal, state);
                    }).ConfigureAwait(false);
                    state = state with { DatabaseIdentity = restoredIdentity, Phase = "database-restored" };
                    OperationSafety.Persist(journal, state);
                    OperationSafety.Persist(databaseOwnershipPath!, databaseOwnership! with { DatabaseIdentity = restoredIdentity });
                }
                SafeFileTree.Exchange(prepared, target, logs, CancellationToken.None, requireAbsent: true);
                if (state.Vhost is not null && !File.Exists(state.Vhost)) File.Copy(vhostBackup, state.Vhost, false);
                await metadataStore.SetWithinOperationAsync(relative, metadata, CancellationToken.None).ConfigureAwait(false);
                if (metadata.StagingIdentity is not null)
                {
                    var ownershipFile = paths.Resolve("app", "config", "staging", OperationSafety.Relative(metadata.StagingIdentity) + ".json");
                    OperationSafety.NoLinks(ownershipFile);
                    var ownership = await store.ReadAsync<StagingOwnership>(ownershipFile).ConfigureAwait(false);
                    if (ownership?.Identity == metadata.StagingIdentity && ownership.ProjectPath == relative)
                        OperationSafety.Persist(ownershipFile, ownership with { RootIdentity = OperationSafety.DirectoryIdentity(target) });
                }
                foreach (var move in snapshotMoves)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(move.Target)!);
                    OperationSafety.MoveDirectory(move.Source, move.Target);
                }
                OperationSafety.Persist(journal, state with { Phase = "rolled-back" });
            }
            catch (Exception exception)
            {
                throw new IOException($"Manuelle Wiederherstellung erforderlich: {state.Recovery}; Zustand {journal}. {exception.Message}", exception);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(prepared) && OperationSafety.DirectoryIdentity(prepared) == preparedIdentity) SafeFileTree.Delete(prepared);
                }
                catch (Exception cleanup) { logs.Write("Staging", $"Vorbereitung erhalten: {prepared}; {cleanup.Message}", LogSeverity.Warning); }
            }
        }
        finally { OperationSafety.Gate.Release(); }
    }
}
