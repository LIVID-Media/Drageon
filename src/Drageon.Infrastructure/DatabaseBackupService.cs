using System.Diagnostics;
using System.Globalization;
using System.Text;
using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed partial class DatabaseBackupService(IPortablePathService paths, JsonFileStore store, IAppLogService logs) : IDatabaseBackupService
{
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
        { "information_schema", "performance_schema", "mysql", "sys" };
    private static readonly SemaphoreSlim _operationGate = new(1, 1);

    internal async Task<string> GetInstanceIdentityAsync(DatabaseServerInfo server, CancellationToken cancellationToken)
    {
        var identity = (await RunClientQueryAsync(server, "SELECT @@hostname, @@datadir, @@port, @@server_id, @@version;", cancellationToken).ConfigureAwait(false)).Trim();
        if (string.IsNullOrWhiteSpace(identity)) throw new InvalidOperationException("Serverinstanz konnte nicht identifiziert werden; unverändert abgebrochen.");
        if (server.ComponentId == "mysql" && !identity.Split('\t').Last().Contains("MariaDB", StringComparison.OrdinalIgnoreCase))
        {
            var uuid = (await RunClientQueryAsync(server, "SELECT @@server_uuid;", cancellationToken).ConfigureAwait(false)).Trim();
            if (string.IsNullOrWhiteSpace(uuid)) throw new InvalidOperationException("MySQL-Server-UUID fehlt; unverändert abgebrochen.");
            identity += "|" + uuid;
        }
        return $"{server.ComponentId}|{server.ClientPath}|{server.Connection.Host}:{server.Connection.Port}|{identity}";
    }

    public async Task<DatabaseServerInfo> GetActiveServerAsync(DatabaseConnection? connection = null, CancellationToken cancellationToken = default)
    {
        var active = await store.ReadAsync<Dictionary<string, string>>(paths.Resolve("app", "config", "packages.active.json"), cancellationToken).ConfigureAwait(false) ?? [];
        var component = active.ContainsKey("mysql") ? "mysql" : active.ContainsKey("mariadb") ? "mariadb" : null;
        if (component is null) throw new InvalidOperationException("Keine aktive MySQL- oder MariaDB-Komponente gefunden.");
        var version = active[component];
        var bin = paths.Resolve("bin", component, version, "bin");
        var clientNames = component == "mysql" ? new[] { "mysql.exe" } : new[] { "mariadb.exe", "mysql.exe" };
        var dumpNames = component == "mysql" ? new[] { "mysqldump.exe" } : new[] { "mariadb-dump.exe", "mysqldump.exe" };
        var client = clientNames.Select(name => Path.Combine(bin, name)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Datenbank-Client fehlt in der aktiven {component}-Version.");
        var dump = dumpNames.Select(name => Path.Combine(bin, name)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"Dump-Werkzeug fehlt in der aktiven {component}-Version.");
        int port; string configuredUser; string protectedPassword;
        var settingsPath = VersionedComponentConfiguration.GetSettingsPath(paths, component, version);
        if (component == "mysql")
        {
            var settings = await VersionedComponentConfiguration.LoadAsync(paths, store, component, version, static () => new MySqlSettings(), cancellationToken).ConfigureAwait(false);
            port = settings.Port; configuredUser = settings.DatabaseUser; protectedPassword = settings.ProtectedPassword;
            if (!string.IsNullOrWhiteSpace(protectedPassword) && !DatabaseCredentialProtector.IsPortable(protectedPassword))
            {
                var legacyPassword = DatabaseCredentialProtector.Unprotect(protectedPassword, paths);
                protectedPassword = string.IsNullOrEmpty(legacyPassword) ? "" : DatabaseCredentialProtector.Protect(legacyPassword, paths);
                await store.WriteAsync(settingsPath, settings with { ProtectedPassword = protectedPassword }, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            var settings = await VersionedComponentConfiguration.LoadAsync(paths, store, component, version, static () => new MariaDbSettings(), cancellationToken).ConfigureAwait(false);
            port = settings.Port; configuredUser = settings.DatabaseUser; protectedPassword = settings.ProtectedPassword;
            if (!string.IsNullOrWhiteSpace(protectedPassword) && !DatabaseCredentialProtector.IsPortable(protectedPassword))
            {
                var legacyPassword = DatabaseCredentialProtector.Unprotect(protectedPassword, paths);
                protectedPassword = string.IsNullOrEmpty(legacyPassword) ? "" : DatabaseCredentialProtector.Protect(legacyPassword, paths);
                await store.WriteAsync(settingsPath, settings with { ProtectedPassword = protectedPassword }, cancellationToken).ConfigureAwait(false);
            }
        }
        var configuredConnection = new DatabaseConnection(Port: port,
            User: string.IsNullOrWhiteSpace(configuredUser) ? "root" : configuredUser.Trim(),
            Password: DatabaseCredentialProtector.Unprotect(protectedPassword, paths));
        return new(component, component == "mysql" ? "MySQL" : "MariaDB", version, client, dump,
            connection ?? configuredConnection);
    }

    public async Task<IReadOnlyList<string>> GetDatabasesAsync(DatabaseConnection? connection = null, CancellationToken cancellationToken = default)
    {
        var server = await GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false);
        var output = await RunClientQueryAsync(server, "SHOW DATABASES;", cancellationToken).ConfigureAwait(false);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !name.Equals("Database", StringComparison.OrdinalIgnoreCase) && !SystemDatabases.Contains(name))
            .Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public Task<IReadOnlyList<DatabaseBackupInfo>> GetBackupsAsync(CancellationToken cancellationToken = default)
    {
        var directory = paths.Resolve("data", "backups", "databases");
        OperationSafety.NoLinks(directory);
        Directory.CreateDirectory(directory);
        IReadOnlyList<DatabaseBackupInfo> result = Directory.EnumerateFiles(directory, "*.sql")
            .Select(path => new FileInfo(path)).Select(file => new DatabaseBackupInfo(file.FullName, ParseDatabaseName(file.Name), file.LastWriteTime, file.Length))
            .OrderByDescending(item => item.CreatedAt).ToArray();
        return Task.FromResult(result);
    }

    public async Task<IReadOnlyList<DatabaseBackupInfo>> BackupAsync(IEnumerable<string> databaseNames, DatabaseConnection? connection = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var names = databaseNames.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0) return [];
        var server = await GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false);
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff", CultureInfo.InvariantCulture);
        var output = new List<DatabaseBackupInfo>();
        foreach (var name in names)
        {
            ValidateDatabaseName(name);
            progress?.Report(name);
            var target = paths.Resolve("data", "backups", "databases", $"{name}_{timestamp}.sql");
            await DumpToPathAsync(server, name, target, cancellationToken).ConfigureAwait(false);
            var file = new FileInfo(target);
            output.Add(new(target, name, file.LastWriteTime, file.Length));
        }
        return output;
    }

    public async Task RestoreAsync(string backupPath, string targetDatabase, DatabaseConnection? connection = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default, string? confirmedPlan = null)
    {
        ValidateDatabaseName(targetDatabase);
        var fullBackup = ValidateSqlPath(backupPath);
        var server = await GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false);
        progress?.Report(targetDatabase);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (confirmedPlan is not null) ProjectSnapshotService.CheckPlan(confirmedPlan, await GetRestorePlanAsync(backupPath, targetDatabase, connection, cancellationToken).ConfigureAwait(false));
            if (await GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false) != server) throw new IOException("Unverändert abgebrochen: Aktive Serverkonfiguration wurde geändert.");
            var existing = (await RunClientQueryAsync(server, "SHOW DATABASES;", cancellationToken).ConfigureAwait(false))
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Contains(targetDatabase, StringComparer.OrdinalIgnoreCase);
            var recovery = paths.Resolve("data", "backups", "databases", $"{targetDatabase}_recovery_{Guid.NewGuid():N}.sql");
            var identity = await GetInstanceIdentityAsync(server, cancellationToken).ConfigureAwait(false);
            var charset = "utf8mb4"; string? collation = null;
            if (existing)
            {
                var schema = await RunClientQueryAsync(server, $"SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME='{EscapeLiteral(targetDatabase)}';", cancellationToken).ConfigureAwait(false);
                var fields = schema.Split(['\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields.Length != 2) throw new InvalidDataException("Schema-Eigenschaften fehlen; unverändert abgebrochen.");
                charset = fields[0]; collation = fields[1]; ValidateSqlToken(charset); ValidateSqlToken(collation);
            }
            var input = recovery + ".input.sql";
            File.Copy(fullBackup, input, false);
            if (existing) await DumpToPathCoreAsync(server, targetDatabase, recovery, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var journal = recovery + ".operation.json";
            var state = new DatabaseRecovery(targetDatabase, identity, existing ? recovery : null, "before-replace", charset, collation, existing ? OperationSafety.Hash(recovery) : null, input);
            OperationSafety.Persist(journal, state);
            var mutated = false;
            try
            {
                // Once a destructive command starts, complete it without the user token; cancellation
                // is observed immediately afterwards, then compensated using an independent token.
                mutated = existing;
                await RunClientQueryAsync(server, (existing ? $"DROP DATABASE {QuoteIdentifier(targetDatabase)}; " : "") + $"CREATE DATABASE {QuoteIdentifier(targetDatabase)} CHARACTER SET {charset}" + (collation is null ? ";" : $" COLLATE {collation};"), CancellationToken.None).ConfigureAwait(false);
                mutated = true;
                cancellationToken.ThrowIfCancellationRequested();
                await ImportFromPathCoreAsync(server, targetDatabase, input, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                OperationSafety.Persist(journal, state with { Phase = "completed" });
            }
            catch (Exception original)
            {
                try
                {
                    if (mutated)
                    {
                        if (await GetInstanceIdentityAsync(server, CancellationToken.None).ConfigureAwait(false) != identity) throw new IOException("Serverinstanz wurde geändert; keine automatische Kompensation.");
                        await RunClientQueryAsync(server, $"DROP DATABASE IF EXISTS {QuoteIdentifier(targetDatabase)};" + (existing ? $" CREATE DATABASE {QuoteIdentifier(targetDatabase)} CHARACTER SET {charset}" + (collation is null ? ";" : $" COLLATE {collation};") : ""), CancellationToken.None).ConfigureAwait(false);
                        if (existing) await ImportFromPathCoreAsync(server, targetDatabase, recovery, CancellationToken.None).ConfigureAwait(false);
                    }
                    OperationSafety.Persist(journal, state with { Phase = "rolled-back" });
                }
                catch (Exception compensation)
                {
                    throw new RecoveryOperationException(RecoveryOutcome.ManualRecoveryRequired, $"Manuelle Wiederherstellung erforderlich: {recovery}; {journal}. {original.Message}; Kompensation: {compensation.Message}", original);
                }
                throw new RecoveryOperationException(mutated ? RecoveryOutcome.RolledBack : RecoveryOutcome.Unchanged, $"{(mutated ? "Erfolgreich zurückgerollt" : "Unverändert abgebrochen")}: {original.Message}. Sicherung: {recovery}", original);
            }
            logs.Write("Database", $"{targetDatabase} aus {Path.GetFileName(fullBackup)} wiederhergestellt.");
        }
        finally { _operationGate.Release(); }
    }

    public async Task DeleteBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        var root = paths.Resolve("data", "backups", "databases");
        var full = Path.GetFullPath(backupPath);
        OperationSafety.NoLinks(full);
        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || !Path.GetExtension(full).Equals(".sql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ungültiger Datenbank-Backup-Pfad.");
        var journal = full + ".operation.json";
        OperationSafety.NoLinks(journal);
        if (File.Exists(journal))
        {
            var state = System.Text.Json.JsonSerializer.Deserialize<DatabaseRecovery>(File.ReadAllText(journal));
            if (state?.Phase is not ("completed" or "rolled-back")) throw new InvalidOperationException($"Unverändert abgebrochen: Rückfalldump wird für eine nicht abgeschlossene Operation benötigt: {journal}");
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(full)) File.Delete(full);
        }
        finally { _operationGate.Release(); }
    }

    public async Task CreateDatabaseFromAsync(string sourceDatabase, string targetDatabase, DatabaseConnection? connection = null, CancellationToken cancellationToken = default)
        => _ = await CreateDatabaseFromCoreAsync(sourceDatabase, targetDatabase, connection, cancellationToken, false).ConfigureAwait(false);

    internal Task<DatabaseStorageIdentity?> CreateOwnedDatabaseFromAsync(string sourceDatabase, string targetDatabase, DatabaseConnection? connection, CancellationToken cancellationToken)
        => CreateDatabaseFromCoreAsync(sourceDatabase, targetDatabase, connection, cancellationToken, true);

    private async Task<DatabaseStorageIdentity?> CreateDatabaseFromCoreAsync(string sourceDatabase, string targetDatabase, DatabaseConnection? connection, CancellationToken cancellationToken, bool trackOwnership)
    {
        ValidateDatabaseName(sourceDatabase); ValidateDatabaseName(targetDatabase);
        if (sourceDatabase.Equals(targetDatabase, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Quell- und Zieldatenbank müssen verschieden sein.");
        var server = await GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false);
        var charsetResult = await RunClientQueryAsync(server,
            $"SELECT DEFAULT_CHARACTER_SET_NAME, DEFAULT_COLLATION_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME='{EscapeLiteral(sourceDatabase)}';", cancellationToken).ConfigureAwait(false);
        var fields = charsetResult.Split(['\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var charset = fields.Length >= 2 ? fields[^2] : "utf8mb4";
        var collation = fields.Length >= 1 ? fields[^1] : null;
        ValidateSqlToken(charset); if (collation is not null) ValidateSqlToken(collation);
        var temp = paths.Resolve("app", "var", "tmp", $"staging-{Guid.NewGuid():N}.sql");
        var identity = await GetInstanceIdentityAsync(server, cancellationToken).ConfigureAwait(false);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var created = false;
        var createAttempted = false;
        var preserveDump = false;
        var journal = temp + ".operation.json";
        DatabaseStorageIdentity? databaseIdentity = null;
        try
        {
            var existingNames = (await RunClientQueryAsync(server, "SHOW DATABASES;", cancellationToken).ConfigureAwait(false)).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (existingNames.Contains(targetDatabase, StringComparer.OrdinalIgnoreCase)) throw new RecoveryOperationException(RecoveryOutcome.Unchanged, $"Unverändert abgebrochen: Datenbank {targetDatabase} existiert bereits.");
            await DumpToPathCoreAsync(server, sourceDatabase, temp, cancellationToken).ConfigureAwait(false);
            OperationSafety.Persist(journal, new DatabaseRecovery(targetDatabase, identity, temp, "before-exclusive-create"));
            var create = $"CREATE DATABASE {QuoteIdentifier(targetDatabase)} CHARACTER SET {charset}"
                + (collation is null ? ";" : $" COLLATE {collation};");
            createAttempted = true;
            await RunClientQueryAsync(server, create, CancellationToken.None).ConfigureAwait(false);
            created = true;
            OperationSafety.Persist(journal, new DatabaseRecovery(targetDatabase, identity, temp, "created-before-import"));
            if (trackOwnership) databaseIdentity = await GetDatabaseStorageIdentityAsync(server, targetDatabase, CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await ImportFromPathCoreAsync(server, targetDatabase, temp, cancellationToken).ConfigureAwait(false);
            OperationSafety.Persist(journal, new DatabaseRecovery(targetDatabase, identity, null, "completed"));
            return databaseIdentity;
        }
        catch (Exception original)
        {
            if (created || createAttempted)
            {
                preserveDump = true;
                OperationSafety.Persist(journal, new DatabaseRecovery(targetDatabase, identity, temp, "manual-recovery"));
                throw new RecoveryOperationException(RecoveryOutcome.ManualRecoveryRequired, $"{original.Message}; Manuelle Wiederherstellung erforderlich: Zieldatenbank {targetDatabase} {(created ? "erzeugt" : "Erzeugungsresultat unklar")}, Server {identity}; Quell-Dump {temp}; Zustand {journal}. Keine Löschung allein anhand des Namens.", original);
            }
            OperationSafety.Persist(journal, new DatabaseRecovery(targetDatabase, identity, null, "rolled-back"));
            throw;
        }
        finally
        {
            if (!preserveDump) { TryDelete(temp); TryDelete(journal); }
            _operationGate.Release();
        }
    }

    public async Task DropDatabaseAsync(string databaseName, DatabaseConnection? connection = null, CancellationToken cancellationToken = default)
    {
        ValidateDatabaseName(databaseName);
        var server = await GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await RunClientQueryAsync(server, $"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)};", cancellationToken).ConfigureAwait(false); }
        finally { _operationGate.Release(); }
    }

    public async Task DumpToPathAsync(DatabaseServerInfo server, string databaseName, string targetPath, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await DumpToPathCoreAsync(server, databaseName, targetPath, cancellationToken).ConfigureAwait(false); }
        finally { _operationGate.Release(); }
    }

    internal async Task ImportFromPathAsync(DatabaseServerInfo server, string databaseName, string sourcePath, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ImportFromPathCoreAsync(server, databaseName, sourcePath, cancellationToken).ConfigureAwait(false); }
        finally { _operationGate.Release(); }
    }

    private async Task DumpToPathCoreAsync(DatabaseServerInfo server, string databaseName, string targetPath, CancellationToken cancellationToken)
    {
        ValidateDatabaseName(databaseName);
        OperationSafety.NoLinks(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var temporary = targetPath + ".partial-" + Guid.NewGuid().ToString("N");
        string? optionFile = null;
        try
        {
            optionFile = await CreateOptionFileAsync(server.Connection, cancellationToken).ConfigureAwait(false);
            var arguments = ConnectionArguments(server.Connection, optionFile).Concat(["--single-transaction", "--routines", "--events", "--triggers", "--add-drop-table", "--default-character-set=utf8mb4", "--", databaseName]);
            var info = CreateStartInfo(server.DumpPath, arguments);
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var process = Process.Start(info) ?? throw new InvalidOperationException("Dump-Werkzeug konnte nicht gestartet werden.");
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var error = (await errorTask.ConfigureAwait(false)).Trim();
                if (process.ExitCode != 0) throw new InvalidOperationException($"Datenbank-Backup fehlgeschlagen: {error}");
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, targetPath, false);
            logs.Write(server.DisplayName, $"Backup für {databaseName} erstellt: {Path.GetFileName(targetPath)}");
        }
        finally { TryDelete(temporary); TryDelete(optionFile); }
    }

    private async Task ImportFromPathCoreAsync(DatabaseServerInfo server, string databaseName, string sourcePath, CancellationToken cancellationToken)
    {
        ValidateDatabaseName(databaseName);
        sourcePath = ValidateSqlPath(sourcePath);
        string? optionFile = null;
        try
        {
            optionFile = await CreateOptionFileAsync(server.Connection, cancellationToken).ConfigureAwait(false);
            var info = CreateStartInfo(server.ClientPath, ConnectionArguments(server.Connection, optionFile).Concat(["--default-character-set=utf8mb4", "--", databaseName]));
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Datenbank-Client konnte nicht gestartet werden.");
            using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } } catch (InvalidOperationException) { } });
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                await source.CopyToAsync(process.StandardInput.BaseStream, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var error = (await errorTask.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Datenbank-Restore fehlgeschlagen: {error}");
            _ = await outputTask.ConfigureAwait(false);
        }
        finally { TryDelete(optionFile); }
    }

    private async Task<string> RunClientQueryAsync(DatabaseServerInfo server, string sql, CancellationToken cancellationToken)
    {
        string? optionFile = null;
        try
        {
            optionFile = await CreateOptionFileAsync(server.Connection, cancellationToken).ConfigureAwait(false);
            var info = CreateStartInfo(server.ClientPath, ConnectionArguments(server.Connection, optionFile).Concat(["--batch", "--skip-column-names", "--execute", sql]));
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Datenbank-Client konnte nicht gestartet werden.");
            using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(); } } catch (InvalidOperationException) { } });
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false); var error = (await errorTask.ConfigureAwait(false)).Trim();
            if (process.ExitCode != 0) throw new InvalidOperationException($"Datenbankbefehl fehlgeschlagen: {error}");
            return output;
        }
        finally { TryDelete(optionFile); }
    }

    private static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        return info;
    }

    private static IEnumerable<string> ConnectionArguments(DatabaseConnection connection, string? optionFile)
    {
        if (optionFile is not null) yield return $"--defaults-extra-file={optionFile}";
        yield return $"--host={connection.Host}"; yield return $"--port={connection.Port}"; yield return $"--user={connection.User}"; yield return "--protocol=tcp";
    }

    private async Task<string?> CreateOptionFileAsync(DatabaseConnection connection, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(connection.Password)) return null;
        var path = paths.Resolve("app", "var", "tmp", $"db-client-{Guid.NewGuid():N}.ini");
        OperationSafety.NoLinks(path);
        var created = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = $"[client]{Environment.NewLine}password=\"{EscapeOptionValue(connection.Password)}\"{Environment.NewLine}";
            await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                created = true;
                await stream.WriteAsync(new UTF8Encoding(false).GetBytes(content), cancellationToken).ConfigureAwait(false);
            }
            File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Temporary);
            return path;
        }
        catch
        {
            if (created) TryDelete(path);
            throw;
        }
    }

    private string ValidateSqlPath(string value)
    {
        var full = Path.GetFullPath(value);
        OperationSafety.NoLinks(full);
        if (!File.Exists(full) || !Path.GetExtension(full).Equals(".sql", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("SQL-Backup nicht gefunden.", full);
        return full;
    }

    private static string ParseDatabaseName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var timestamp = System.Text.RegularExpressions.Regex.Match(stem, @"^(.*)_\d{4}-\d{2}-\d{2}_\d{6}(?:_\d{3}|\.\d{3})?(?:-[a-f0-9]{8,32})?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (timestamp.Success) return timestamp.Groups[1].Value;
        var marker = stem.LastIndexOf('_');
        if (marker > 0) marker = stem.LastIndexOf('_', marker - 1);
        return marker > 0 ? stem[..marker] : stem;
    }
    private static void ValidateDatabaseName(string value) { if (SystemDatabases.Contains(value) || string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-'))) throw new ArgumentException("Ungültiger Datenbankname.", nameof(value)); }
    private static void ValidateSqlToken(string value) { if (value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_'))) throw new InvalidDataException("Ungültiger Zeichensatz oder Kollationsname."); }
    private static string QuoteIdentifier(string value) => $"`{value.Replace("`", "``", StringComparison.Ordinal)}`";
    private static string EscapeLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string EscapeOptionValue(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
    private static void TryDelete(string? path) { if (path is null) return; try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
