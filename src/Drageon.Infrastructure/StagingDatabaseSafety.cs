using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed partial class DatabaseBackupService
{
    // Physical identities prove both the local server storage and the CREATE'd schema incarnation.
    // Remote, linked or encoded schema paths are deliberately unsupported for automatic deletion.
    internal async Task<DatabaseStorageIdentity> GetDatabaseStorageIdentityAsync(DatabaseServerInfo server, string database, CancellationToken token, bool requireDatabase = true)
    {
        ValidateDatabaseName(database);
        if (database.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '_')) || server.Connection.Host is not ("127.0.0.1" or "localhost" or "::1"))
            throw new InvalidOperationException("Automatische Staging-Datenbanklöschung benötigt einen lokalen Server und einen Datenbanknamen aus Buchstaben, Ziffern und Unterstrichen.");
        var data = Path.TrimEndingDirectorySeparator(Path.GetFullPath((await RunClientQueryAsync(server, "SELECT @@datadir;", token).ConfigureAwait(false)).Trim()));
        if (!OperationSafety.Contains(paths.AppRoot, data) || data.Equals(paths.AppRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Datenbank-Datenverzeichnis liegt außerhalb dieser Drageon-Instanz; kein sicherer Erzeugungsnachweis möglich.");
        OperationSafety.NoLinks(data);
        var system = OperationSafety.DirectoryIdentity(Path.Combine(data, "mysql"));
        var schema = Path.Combine(data, database);
        OperationSafety.NoLinks(schema);
        return new(data, OperationSafety.DirectoryIdentity(data), system, requireDatabase ? OperationSafety.DirectoryIdentity(schema) : "");
    }

    internal async Task<OwnedDatabaseOperation> AcquireOwnedDatabaseAsync(string database, string serverIdentity, DatabaseStorageIdentity proof, DatabaseConnection? connection, CancellationToken token, bool allowMissing = false)
    {
        await _operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var server = await GetActiveServerAsync(connection, token).ConfigureAwait(false);
            if (await GetInstanceIdentityAsync(server, token).ConfigureAwait(false) != serverIdentity) throw new InvalidOperationException("Unverändert abgebrochen: Staging-Datenbankserver wurde geändert.");
            var storage = await GetDatabaseStorageIdentityAsync(server, database, token, false).ConfigureAwait(false);
            if (storage.DataDirectory != proof.DataDirectory || storage.DataDirectoryIdentity != proof.DataDirectoryIdentity || storage.SystemDirectoryIdentity != proof.SystemDirectoryIdentity)
                throw new InvalidOperationException("Unverändert abgebrochen: Datenbankserver-Speicher wurde ersetzt.");
            var exists = (await GetDatabasesAsync(server.Connection, token).ConfigureAwait(false)).Contains(database, StringComparer.OrdinalIgnoreCase);
            if (exists && await GetDatabaseStorageIdentityAsync(server, database, token).ConfigureAwait(false) != proof)
                throw new InvalidOperationException("Unverändert abgebrochen: Gleichnamige Staging-Datenbank wurde ersetzt; fremder Bestand bleibt erhalten.");
            if (!exists && !allowMissing) throw new InvalidOperationException("Unverändert abgebrochen: Nachgewiesene Staging-Datenbank fehlt.");
            return new(this, server, database, serverIdentity, proof, exists);
        }
        catch { _operationGate.Release(); throw; }
    }

    internal sealed class OwnedDatabaseOperation(DatabaseBackupService service, DatabaseServerInfo server, string database, string serverIdentity, DatabaseStorageIdentity proof, bool exists) : IDisposable
    {
        public async Task<(string CharacterSet, string? Collation)> BackupAsync(string file, CancellationToken token)
        {
            var fields = (await service.RunClientQueryAsync(server, $"SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME='{EscapeLiteral(database)}';", token).ConfigureAwait(false)).Split(['\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length != 2) throw new InvalidDataException("Schema-Eigenschaften der Staging-Datenbank fehlen.");
            ValidateSqlToken(fields[0]); ValidateSqlToken(fields[1]);
            await service.DumpToPathCoreAsync(server, database, file, token).ConfigureAwait(false);
            return (fields[0], fields[1]);
        }

        public async Task DropAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (await service.GetInstanceIdentityAsync(server, token).ConfigureAwait(false) != serverIdentity
                || await service.GetDatabaseStorageIdentityAsync(server, database, token).ConfigureAwait(false) != proof)
                throw new IOException("Staging-Datenbank wurde zwischenzeitlich ersetzt; keine Löschung.");
            OperationSafety.Checkpoint?.Invoke("staging-database-drop");
            await service.RunClientQueryAsync(server, $"DROP DATABASE {QuoteIdentifier(database)};", CancellationToken.None).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }

        public async Task<DatabaseStorageIdentity> RestoreAsync(string file, string charset, string? collation, Action<DatabaseStorageIdentity> created)
        {
            if (exists) return proof;
            ValidateSqlToken(charset); if (collation is not null) ValidateSqlToken(collation);
            await service.RunClientQueryAsync(server, $"CREATE DATABASE {QuoteIdentifier(database)} CHARACTER SET {charset}" + (collation is null ? ";" : $" COLLATE {collation};"), CancellationToken.None).ConfigureAwait(false);
            var identity = await service.GetDatabaseStorageIdentityAsync(server, database, CancellationToken.None).ConfigureAwait(false);
            created(identity);
            await service.ImportFromPathCoreAsync(server, database, file, CancellationToken.None).ConfigureAwait(false);
            return identity;
        }

        public void Dispose() => _operationGate.Release();
    }
}
