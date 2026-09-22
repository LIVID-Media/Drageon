using System.Diagnostics;
using System.Globalization;
using System.Text;
using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed class DatabaseBackupService(IPortablePathService paths, JsonFileStore store, IAppLogService logs) : IDatabaseBackupService
{
    private static readonly HashSet<string> SystemDatabases = new(StringComparer.OrdinalIgnoreCase)
        { "information_schema", "performance_schema", "mysql", "sys" };
    private readonly SemaphoreSlim _operationGate = new(1, 1);

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
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
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

    public async Task RestoreAsync(string backupPath, string targetDatabase, DatabaseConnection? connection = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ValidateDatabaseName(targetDatabase);
        var fullBackup = ValidateSqlPath(backupPath);
        var server = await GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false);
        progress?.Report(targetDatabase);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunClientQueryAsync(server, $"DROP DATABASE IF EXISTS {QuoteIdentifier(targetDatabase)}; CREATE DATABASE {QuoteIdentifier(targetDatabase)} CHARACTER SET utf8mb4;", cancellationToken).ConfigureAwait(false);
            await ImportFromPathCoreAsync(server, targetDatabase, fullBackup, cancellationToken).ConfigureAwait(false);
            logs.Write("Database", $"{targetDatabase} aus {Path.GetFileName(fullBackup)} wiederhergestellt.");
        }
        finally { _operationGate.Release(); }
    }

    public Task DeleteBackupAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = paths.Resolve("data", "backups", "databases");
        var full = Path.GetFullPath(backupPath);
        var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || !Path.GetExtension(full).Equals(".sql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Ungültiger Datenbank-Backup-Pfad.");
        if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    public async Task CreateDatabaseFromAsync(string sourceDatabase, string targetDatabase, DatabaseConnection? connection = null, CancellationToken cancellationToken = default)
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
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DumpToPathCoreAsync(server, sourceDatabase, temp, cancellationToken).ConfigureAwait(false);
            var create = $"DROP DATABASE IF EXISTS {QuoteIdentifier(targetDatabase)}; CREATE DATABASE {QuoteIdentifier(targetDatabase)} CHARACTER SET {charset}"
                + (collation is null ? ";" : $" COLLATE {collation};");
            await RunClientQueryAsync(server, create, cancellationToken).ConfigureAwait(false);
            await ImportFromPathCoreAsync(server, targetDatabase, temp, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(temp);
            _operationGate.Release();
        }
    }

    public async Task DropDatabaseAsync(string databaseName, DatabaseConnection? connection = null, CancellationToken cancellationToken = default)
    {
        ValidateDatabaseName(databaseName);
        var server = await GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false);
        await RunClientQueryAsync(server, $"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)};", cancellationToken).ConfigureAwait(false);
    }

    public async Task DumpToPathAsync(DatabaseServerInfo server, string databaseName, string targetPath, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await DumpToPathCoreAsync(server, databaseName, targetPath, cancellationToken).ConfigureAwait(false); }
        finally { _operationGate.Release(); }
    }

    public async Task ImportFromPathAsync(DatabaseServerInfo server, string databaseName, string sourcePath, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await ImportFromPathCoreAsync(server, databaseName, sourcePath, cancellationToken).ConfigureAwait(false); }
        finally { _operationGate.Release(); }
    }

    private async Task DumpToPathCoreAsync(DatabaseServerInfo server, string databaseName, string targetPath, CancellationToken cancellationToken)
    {
        ValidateDatabaseName(databaseName);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var temporary = targetPath + ".partial";
        string? optionFile = null;
        try
        {
            optionFile = await CreateOptionFileAsync(server.Connection, cancellationToken).ConfigureAwait(false);
            var arguments = ConnectionArguments(server.Connection, optionFile).Concat(["--single-transaction", "--routines", "--events", "--triggers", "--add-drop-table", "--default-character-set=utf8mb4", databaseName]);
            var info = CreateStartInfo(server.DumpPath, arguments);
            await using (var destination = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                using var process = Process.Start(info) ?? throw new InvalidOperationException("Dump-Werkzeug konnte nicht gestartet werden.");
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.StandardOutput.BaseStream.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                var error = (await errorTask.ConfigureAwait(false)).Trim();
                if (process.ExitCode != 0) throw new InvalidOperationException($"Datenbank-Backup fehlgeschlagen: {error}");
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, targetPath, true);
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
            var info = CreateStartInfo(server.ClientPath, ConnectionArguments(server.Connection, optionFile).Concat(["--default-character-set=utf8mb4", databaseName]));
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Datenbank-Client konnte nicht gestartet werden.");
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
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var content = $"[client]{Environment.NewLine}password=\"{EscapeOptionValue(connection.Password)}\"{Environment.NewLine}";
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.SetAttributes(path, FileAttributes.Hidden | FileAttributes.Temporary);
            return path;
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    private string ValidateSqlPath(string value)
    {
        var full = Path.GetFullPath(value);
        if (!File.Exists(full) || !Path.GetExtension(full).Equals(".sql", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("SQL-Backup nicht gefunden.", full);
        return full;
    }

    private static string ParseDatabaseName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var marker = stem.LastIndexOf('_');
        if (marker > 0) marker = stem.LastIndexOf('_', marker - 1);
        return marker > 0 ? stem[..marker] : stem;
    }
    private static void ValidateDatabaseName(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_' or '-'))) throw new ArgumentException("Ungültiger Datenbankname.", nameof(value)); }
    private static void ValidateSqlToken(string value) { if (value.Any(ch => !(char.IsLetterOrDigit(ch) || ch is '_'))) throw new InvalidDataException("Ungültiger Zeichensatz oder Kollationsname."); }
    private static string QuoteIdentifier(string value) => $"`{value.Replace("`", "``", StringComparison.Ordinal)}`";
    private static string EscapeLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string EscapeOptionValue(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal);
    private static void TryDelete(string? path) { if (path is null) return; try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
