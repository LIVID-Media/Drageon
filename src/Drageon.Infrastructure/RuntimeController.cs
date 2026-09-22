using Drageon.Core;
using System.Diagnostics;

namespace Drageon.Infrastructure;

public sealed class RuntimeController(
    IPortablePathService paths,
    JsonFileStore store,
    IProcessManager processes,
    IEnumerable<IConfigGenerator> generators,
    IWebServerRegistry webServers,
    IAppLogService? logs = null,
    ILanguageService? language = null) : IRuntimeController
{
    private readonly IReadOnlyDictionary<string, IConfigGenerator> _generators = generators.ToDictionary(static item => item.ComponentId);
    private readonly SemaphoreSlim _webServerGate = new(1, 1);
    public event EventHandler? WebServerStarted;
    private async Task<string> SelectedWebServerAsync(CancellationToken token) =>
        (await store.ReadAsync<AppSettings>(paths.Resolve("app", "config", "settings.json"), token) ?? new()).WebServer;

    public async Task StartAsync(string componentId, CancellationToken cancellationToken = default)
    {
        componentId = componentId.ToLowerInvariant();
        if (webServers.TryGet(componentId, out var webServer))
        {
            await _webServerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!componentId.Equals(await SelectedWebServerAsync(cancellationToken), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(T("webserver.selectFirst", "Wähle diesen Webserver zuerst in den Einstellungen aus."));
                foreach (var other in webServers.All.Where(server => server.Id != componentId))
                    await other.StopAsync(cancellationToken).ConfigureAwait(false);
                await webServer.StartAsync(cancellationToken).ConfigureAwait(false);
                WebServerStarted?.Invoke(this, EventArgs.Empty);
            }
            finally { _webServerGate.Release(); }
            return;
        }
        if (_generators.TryGetValue(componentId, out var generator))
        {
            await generator.GenerateAsync(cancellationToken).ConfigureAwait(false);
        }

        var active = await store.ReadAsync<Dictionary<string, string>>(
            paths.Resolve("app", "config", "packages.active.json"), cancellationToken).ConfigureAwait(false) ?? [];
        if (!active.TryGetValue(componentId, out var version))
        {
            throw new InvalidOperationException(TF("runtime.noActiveVersion", "Für {0} ist keine Version aktiviert.", componentId));
        }

        var definition = componentId switch
        {
            "mariadb" => new ProcessDefinition(
                "mariadb",
                paths.Resolve("bin", "mariadb", version, "bin", "mysqld.exe"),
                $"--defaults-file=\"{Path.Combine(VersionedComponentConfiguration.GetGeneratedDirectory(paths, "mariadb", version), "my.ini")}\" --console",
                paths.Resolve("bin", "mariadb", version),
                paths.Resolve("app", "var", "logs", "mariadb", "console.log")),
            "mysql" => new ProcessDefinition(
                "mysql",
                paths.Resolve("bin", "mysql", version, "bin", "mysqld.exe"),
                $"--defaults-file=\"{Path.Combine(VersionedComponentConfiguration.GetGeneratedDirectory(paths, "mysql", version), "my.ini")}\" --console",
                paths.Resolve("bin", "mysql", version),
                paths.Resolve("app", "var", "logs", "mysql", "console.log")),
            _ => throw new NotSupportedException(TF("runtime.notServer", "{0} ist kein eigenständiger Serverprozess.", componentId))
        };

        if (PackageCatalog.IsDatabase(componentId))
        {
            await EnsureDatabaseInitializedAsync(componentId, version, cancellationToken).ConfigureAwait(false);
        }


        var status = await processes.StartAsync(definition, cancellationToken).ConfigureAwait(false);
        if (status.State == ComponentState.Failed)
        {
            throw new InvalidOperationException(status.Error);
        }
    }

    public async Task StopAsync(string componentId, CancellationToken cancellationToken = default)
    {
        componentId = componentId.ToLowerInvariant();
        if (webServers.TryGet(componentId, out var webServer))
        {
            await _webServerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await webServer.StopAsync(cancellationToken).ConfigureAwait(false); }
            finally { _webServerGate.Release(); }
            return;
        }
        var graceful = PackageCatalog.IsDatabase(componentId)
            && await TryShutdownDatabaseAsync(componentId, cancellationToken).ConfigureAwait(false);
        if (graceful)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (!processes.GetStatuses().Any(status => status.Id.Equals(componentId, StringComparison.OrdinalIgnoreCase)
                    && status.State is ComponentState.Running or ComponentState.Starting)) return;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        await processes.StopAsync(componentId, TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAllAsync(CancellationToken cancellationToken = default)
    {
        var active = await store.ReadAsync<Dictionary<string, string>>(
            paths.Resolve("app", "config", "packages.active.json"), cancellationToken).ConfigureAwait(false) ?? [];
        var errors = new List<string>();

        var databaseId = GetActiveDatabaseId(active);
        if (databaseId is not null)
        {
            try
            {
                await StartAsync(databaseId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add($"{DatabaseDisplayName(databaseId)}: {exception.Message}");
            }
        }

        var selectedWebServer = await SelectedWebServerAsync(cancellationToken);
        if (active.ContainsKey(selectedWebServer))
        {
            try
            {
                await StartAsync(selectedWebServer, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add($"{selectedWebServer}: {exception.Message}");
            }
        }

        if (databaseId is null && !active.ContainsKey(selectedWebServer))
        {
            throw new InvalidOperationException(T("runtime.installFirst", "Installiere und aktiviere zuerst den gewählten Webserver oder eine Datenbank."));
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        var running = processes.GetStatuses()
            .Where(static status => status.State is ComponentState.Running or ComponentState.Starting)
            .Select(static status => status.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var server in webServers.All) await StopAsync(server.Id, cancellationToken).ConfigureAwait(false);
        if (running.Contains("mysql")) await StopAsync("mysql", cancellationToken).ConfigureAwait(false);
        if (running.Contains("mariadb")) await StopAsync("mariadb", cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureDatabaseInitializedAsync(string componentId, string version, CancellationToken cancellationToken)
    {
        var dataDirectory = VersionedComponentConfiguration.GetDataDirectory(paths, componentId, version);
        if (Directory.Exists(Path.Combine(dataDirectory, "mysql"))) return;

        Directory.CreateDirectory(dataDirectory);
        var displayName = DatabaseDisplayName(componentId);
        var binDirectory = paths.Resolve("bin", componentId, version, "bin");
        logs?.Write(displayName, T("runtime.databaseInitializing", "Initialisiere das portable Datenverzeichnis …"));

        if (componentId == "mysql")
        {
            var server = Path.Combine(binDirectory, "mysqld.exe");
            if (!File.Exists(server))
            {
                throw new FileNotFoundException(T("runtime.mysqlInitializerMissing", "MySQL kann nicht initialisiert werden: mysqld.exe fehlt."));
            }
            var baseDirectory = paths.Resolve("bin", componentId, version);
            await RunToolAsync(displayName, server,
                $"--initialize-insecure --console --basedir=\"{baseDirectory}\" --datadir=\"{dataDirectory}\"",
                baseDirectory, cancellationToken).ConfigureAwait(false);
            return;
        }

        var initializer = new[] { "mariadb-install-db.exe", "mysql_install_db.exe" }
            .Select(name => Path.Combine(binDirectory, name))
            .FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException(T("runtime.mariadbInitializerMissing", "MariaDB kann nicht initialisiert werden: mariadb-install-db.exe fehlt."));
        var settings = await VersionedComponentConfiguration.LoadAsync(
            paths, store, componentId, version, static () => new MariaDbSettings(), cancellationToken).ConfigureAwait(false);
        await RunToolAsync(displayName, initializer,
            $"--datadir=\"{dataDirectory}\" --port={settings.Port}", binDirectory, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunToolAsync(string component, string executable, string arguments, string workingDirectory, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables = null, bool writeProcessOutputToLog = true)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (environmentVariables is not null)
        {
            foreach (var variable in environmentVariables) startInfo.Environment[variable.Key] = variable.Value;
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException(TF("runtime.helperStartFailed", "{0}: Hilfsprogramm konnte nicht gestartet werden.", component));
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await outputTask.ConfigureAwait(false)).Trim();
        var error = (await errorTask.ConfigureAwait(false)).Trim();
        if (writeProcessOutputToLog && output.Length > 0) logs?.Write(component, output);
        if (writeProcessOutputToLog && error.Length > 0) logs?.Write(component, error, process.ExitCode == 0 ? LogSeverity.Information : LogSeverity.Error);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(TF("runtime.startFailed", "{0} konnte nicht gestartet werden: {1}", component, error.Length > 0 ? error : output));
        }
    }

    private async Task<bool> TryShutdownDatabaseAsync(string componentId, CancellationToken cancellationToken)
    {
        if (!processes.GetStatuses().Any(status => status.Id.Equals(componentId, StringComparison.OrdinalIgnoreCase)
            && status.State is ComponentState.Running or ComponentState.Starting)) return false;
        var active = await store.ReadAsync<Dictionary<string, string>>(
            paths.Resolve("app", "config", "packages.active.json"), cancellationToken).ConfigureAwait(false) ?? [];
        if (!active.TryGetValue(componentId, out var version)) return false;
        var binDirectory = paths.Resolve("bin", componentId, version, "bin");
        var admin = new[] { "mariadb-admin.exe", "mysqladmin.exe" }
            .Select(name => Path.Combine(binDirectory, name)).FirstOrDefault(File.Exists);
        if (admin is null) return false;
        var port = componentId == "mysql"
            ? (await VersionedComponentConfiguration.LoadAsync(
                paths, store, componentId, version, static () => new MySqlSettings(), cancellationToken).ConfigureAwait(false)).Port
            : (await VersionedComponentConfiguration.LoadAsync(
                paths, store, componentId, version, static () => new MariaDbSettings(), cancellationToken).ConfigureAwait(false)).Port;
        var displayName = DatabaseDisplayName(componentId);
        try
        {
            await RunToolAsync(displayName, admin,
                $"--protocol=tcp --host=127.0.0.1 --port={port} --user=root shutdown",
                binDirectory, cancellationToken, writeProcessOutputToLog: false).ConfigureAwait(false);
            logs?.Write(displayName, T("runtime.databaseStopped", "Datenbank wurde kontrolliert heruntergefahren."));
            return true;
        }
        catch (InvalidOperationException)
        {
            // Falls der direkte Admin-Aufruf keine Anmeldung erhält, übernimmt der Prozessmanager
            // das lokale Beenden. Zugangsdaten werden an dieser Stelle nie als Argument übergeben.
            return false;
        }
    }

    private static string? GetActiveDatabaseId(IReadOnlyDictionary<string, string> active) =>
        active.ContainsKey("mysql") ? "mysql" : active.ContainsKey("mariadb") ? "mariadb" : null;

    private static string DatabaseDisplayName(string componentId) => componentId == "mysql" ? "MySQL" : "MariaDB";

    private string T(string key, string fallback) => language?.Get(key, fallback) ?? fallback;
    private string TF(string key, string fallback, params object?[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, T(key, fallback), arguments);
}


