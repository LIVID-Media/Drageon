using Drageon.Core;

namespace Drageon.Infrastructure;

internal sealed record SnapshotDatabaseState(string Name, string Server, DatabaseStorageIdentity Storage, bool Existed,
    string CharacterSet, string? Collation, string? BackupHash, DatabaseStorageIdentity? Created = null, string Phase = "prepared");

public sealed partial class DatabaseBackupService
{
    internal async Task<SnapshotDatabaseOperation> AcquireSnapshotDatabaseAsync(string name, string? expectedServer, DatabaseConnection? connection, CancellationToken token)
    {
        await _operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ValidateDatabaseName(name);
            var server = await GetActiveServerAsync(connection, token).ConfigureAwait(false);
            var identity = await GetInstanceIdentityAsync(server, token).ConfigureAwait(false);
            if (expectedServer is not null && expectedServer != identity) throw new IOException("Snapshot gehört zu einer anderen Datenbankserver-Instanz.");
            var exists = (await GetDatabasesAsync(connection, token).ConfigureAwait(false)).Contains(name, StringComparer.OrdinalIgnoreCase);
            // Physical incarnation checks keep compensation from deleting a foreign replacement schema.
            var storage = await GetDatabaseStorageIdentityAsync(server, name, token, exists).ConfigureAwait(false);
            return new(this, server, name, identity, storage, exists);
        }
        catch { _operationGate.Release(); throw; }
    }

    internal sealed class SnapshotDatabaseOperation(DatabaseBackupService service, DatabaseServerInfo server, string name, string identity, DatabaseStorageIdentity storage, bool exists) : IDisposable
    {
        public string Fingerprint => System.Text.Json.JsonSerializer.Serialize(new { identity, storage, exists });
        public async Task<SnapshotDatabaseState> BackupAsync(string path, CancellationToken token)
        {
            var charset = "utf8mb4"; string? collation = null;
            if (exists)
            {
                var fields = (await service.RunClientQueryAsync(server, $"SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME='{EscapeLiteral(name)}';", token).ConfigureAwait(false)).Split(['\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields.Length != 2) throw new InvalidDataException("Schema-Eigenschaften fehlen.");
                charset = fields[0]; collation = fields[1]; ValidateSqlToken(charset); ValidateSqlToken(collation);
                await service.DumpToPathCoreAsync(server, name, path, token).ConfigureAwait(false);
            }
            return new(name, identity, storage, exists, charset, collation, exists ? OperationSafety.Hash(path) : null);
        }

        public async Task ApplyAsync(string sql, SnapshotDatabaseState state, Action<SnapshotDatabaseState> persist, CancellationToken token)
        {
            await CheckServer(state, token).ConfigureAwait(false);
            var present = (await service.GetDatabasesAsync(server.Connection, token).ConfigureAwait(false)).Contains(name, StringComparer.OrdinalIgnoreCase);
            if (present != state.Existed || present && await service.GetDatabaseStorageIdentityAsync(server, name, token).ConfigureAwait(false) != state.Storage)
                throw new IOException("Zieldatenbank wurde zwischenzeitlich ersetzt.");
            token.ThrowIfCancellationRequested();
            persist(state with { Phase = "before-drop" });
            if (present) await service.RunClientQueryAsync(server, $"DROP DATABASE {QuoteIdentifier(name)};", CancellationToken.None).ConfigureAwait(false);
            persist(state with { Phase = "dropped" });
            await Create(state).ConfigureAwait(false);
            state = state with { Created = await service.GetDatabaseStorageIdentityAsync(server, name, CancellationToken.None).ConfigureAwait(false), Phase = "created" };
            persist(state); // Persist CREATE identity before the cancellable import.
            token.ThrowIfCancellationRequested();
            OperationSafety.Checkpoint?.Invoke("snapshot-database-import");
            await service.ImportFromPathCoreAsync(server, name, sql, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            persist(state with { Phase = "imported" });
        }

        public async Task RollbackAsync(SnapshotDatabaseState state, string backup, Action<SnapshotDatabaseState> persist)
        {
            await CheckServer(state, CancellationToken.None).ConfigureAwait(false);
            if (state.Existed && (state.BackupHash is null || OperationSafety.Hash(backup) != state.BackupHash)) throw new IOException("Datenbank-Rückfalldump: Integritätsfehler.");
            var present = (await service.GetDatabasesAsync(server.Connection, CancellationToken.None).ConfigureAwait(false)).Contains(name, StringComparer.OrdinalIgnoreCase);
            if (present)
            {
                var current = await service.GetDatabaseStorageIdentityAsync(server, name, CancellationToken.None).ConfigureAwait(false);
                if (current == state.Storage && state.Phase is "prepared" or "before-drop") return;
                if (state.Created is null || current != state.Created) throw new IOException("Gleichnamige fremde Datenbank bleibt erhalten; SQL-Dump manuell wiederherstellen.");
                persist(state with { Phase = "rollback-before-drop" });
                await service.RunClientQueryAsync(server, $"DROP DATABASE {QuoteIdentifier(name)};", CancellationToken.None).ConfigureAwait(false);
            }
            if (state.Existed)
            {
                await Create(state).ConfigureAwait(false);
                state = state with { Created = await service.GetDatabaseStorageIdentityAsync(server, name, CancellationToken.None).ConfigureAwait(false), Phase = "rollback-created" };
                persist(state);
                await service.ImportFromPathCoreAsync(server, name, backup, CancellationToken.None).ConfigureAwait(false);
            }
            persist(state with { Phase = "rolled-back" });
        }

        private Task<string> Create(SnapshotDatabaseState state) => service.RunClientQueryAsync(server, $"CREATE DATABASE {QuoteIdentifier(name)} CHARACTER SET {state.CharacterSet}" + (state.Collation is null ? ";" : $" COLLATE {state.Collation};"), CancellationToken.None);
        private async Task CheckServer(SnapshotDatabaseState state, CancellationToken token)
        {
            var current = await service.GetDatabaseStorageIdentityAsync(server, name, token, false).ConfigureAwait(false);
            if (await service.GetInstanceIdentityAsync(server, token).ConfigureAwait(false) != state.Server || current.DataDirectoryIdentity != state.Storage.DataDirectoryIdentity || current.SystemDirectoryIdentity != state.Storage.SystemDirectoryIdentity || current.DataDirectory != state.Storage.DataDirectory)
                throw new IOException("Datenbankserver oder Speicher wurde ersetzt; keine automatische Kompensation.");
        }
        public void Dispose() => _operationGate.Release();
    }
}
