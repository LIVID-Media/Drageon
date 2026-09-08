using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Drageon.Core;

namespace Drageon.Infrastructure;

public abstract class WebServerBase(IPortablePathService paths, JsonFileStore store, IProcessManager processes,
    IReadOnlyDictionary<string, IConfigGenerator> generators, IAppLogService? logs, ILanguageService? language) : IWebServer
{
    protected IPortablePathService Paths => paths;
    protected JsonFileStore Store => store;
    protected IProcessManager Processes => processes;
    protected IAppLogService? Logs => logs;
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public virtual IReadOnlyList<string> RequiredPackageIds => [Id];
    public virtual string? PhpRuntimeProcessId => null;
    public virtual bool UsesFastCgiPort => false;
    public abstract Task StartAsync(CancellationToken cancellationToken = default);
    public virtual Task StopAsync(CancellationToken cancellationToken = default) => processes.StopAsync(Id, TimeSpan.FromSeconds(8), cancellationToken);
    protected bool Running(string id) => processes.GetStatuses().Any(s => s.Id == id && s.State is ComponentState.Running or ComponentState.Starting);
    protected string T(string key, string fallback) => language?.Get(key, fallback) ?? fallback;

    protected async Task<Dictionary<string, string>> PrepareAsync(CancellationToken token)
    {
        var active = await store.ReadAsync<Dictionary<string, string>>(paths.Resolve("app", "config", "packages.active.json"), token) ?? [];
        if (!active.ContainsKey(Id)) throw new InvalidOperationException(string.Format(T("runtime.noActiveVersion", "No active version for {0}."), Id));
        foreach (var id in new[] { "php", "phpmyadmin", Id })
            if (generators.TryGetValue(id, out var generator)) await generator.GenerateAsync(token);
        return active;
    }

    protected Dictionary<string, string> PhpEnvironment(string version)
    {
        var root = paths.Resolve("bin", "php", version);
        var config = PhpVersionConfiguration.GetGeneratedDirectory(paths, version);
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = root + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
            ["PHPRC"] = config,
            ["PHP_PATH"] = Path.Combine(root, "php.exe"),
            ["PHP_PEAR_PHP_BIN"] = Path.Combine(root, "php.exe"),
            ["DRAGEON_PHP_BINARY"] = Path.Combine(root, "php.exe"),
            ["DRAGEON_PHP_INI"] = Path.Combine(config, "php.ini"),
            ["DRAGEON_ROOT"] = paths.AppRoot
        };
    }

    protected async Task CheckPortsAsync(int httpPort, CancellationToken token)
    {
        var hosts = await new VirtualHostStore(paths, store).ReadAsync(token);
        foreach (var port in hosts.Where(h => h.Enabled).Select(h => h.Port ?? httpPort).Append(httpPort).Distinct()) CheckPort(port, IPAddress.Any);
    }

    protected void CheckPort(int port, IPAddress address)
    {
        if (port is < 1 or > 65535) throw new InvalidOperationException(T("settings.error.ports", "Invalid port."));
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        try { socket.Bind(new IPEndPoint(address, port)); }
        catch (SocketException ex) { throw new InvalidOperationException(string.Format(T("webserver.portBusy", "Port {0} is unavailable."), port), ex); }
    }

    protected async Task StartProcessAsync(ProcessDefinition definition, CancellationToken token)
    {
        var status = await processes.StartAsync(definition, token);
        token.ThrowIfCancellationRequested();
        if (status.State != ComponentState.Running) throw new InvalidOperationException(status.Error ?? T("webserver.processExited", "Server process exited during startup."));
    }

    protected async Task RunToolAsync(ProcessDefinition definition, string extra, CancellationToken token)
    {
        var info = new ProcessStartInfo(definition.ExecutablePath, $"{definition.Arguments} {extra}")
        {
            WorkingDirectory = definition.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, RedirectStandardOutput = true
        };
        if (definition.EnvironmentVariables is not null)
            foreach (var variable in definition.EnvironmentVariables) info.Environment[variable.Key] = variable.Value;
        using var process = Process.Start(info) ?? throw new InvalidOperationException(T("webserver.processExited", "Could not start helper."));
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var message = ((await output) + (await error)).Trim();
        if (message.Length > 0) logs?.Write(Id, message, process.ExitCode == 0 ? LogSeverity.Information : LogSeverity.Error);
        if (process.ExitCode != 0) throw new InvalidOperationException(message);
    }
}

public sealed class ApacheWebServer(IPortablePathService paths, JsonFileStore store, IProcessManager processes,
    IReadOnlyDictionary<string, IConfigGenerator> generators, IAppLogService? logs = null, ILanguageService? language = null)
    : WebServerBase(paths, store, processes, generators, logs, language)
{
    public override string Id => "apache";
    public override string DisplayName => "Apache";
    public override async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (Running(Id)) return;
        var active = await PrepareAsync(cancellationToken);
        var version = active[Id];
        var settings = await Store.ReadAsync<AppSettings>(Paths.Resolve("app", "config", "settings.json"), cancellationToken) ?? new();
        await CheckPortsAsync(settings.HttpPort, cancellationToken);
        var definition = new ProcessDefinition(Id, Paths.Resolve("bin", Id, version, "bin", "httpd.exe"),
            $"-d \"{Paths.AppRoot.Replace('\\', '/')}\" -f app/var/generated/apache/{version}/httpd.conf",
            Paths.Resolve("bin", Id, version), Paths.Resolve("app", "var", "logs", Id, "console.log"),
            active.TryGetValue("php", out var php) ? PhpEnvironment(php) : null);
        await RunToolAsync(definition, "-t", cancellationToken);
        try { await StartProcessAsync(definition, cancellationToken); }
        catch { await StopAsync(CancellationToken.None); throw; }
    }
}

public sealed class NginxWebServer(IPortablePathService paths, JsonFileStore store, IProcessManager processes,
    IReadOnlyDictionary<string, IConfigGenerator> generators, IAppLogService? logs = null, ILanguageService? language = null)
    : WebServerBase(paths, store, processes, generators, logs, language)
{
    public const string FastCgiId = "php-fastcgi";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProcessDefinition? _definition;
    private CancellationTokenSource? _monitor;
    public override string Id => "nginx";
    public override string DisplayName => "Nginx";
    public override IReadOnlyList<string> RequiredPackageIds => [Id, "php"];
    public override string PhpRuntimeProcessId => FastCgiId;
    public override bool UsesFastCgiPort => true;

    public override async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (Running(Id) && Running(FastCgiId)) return;
            await StopCoreAsync(CancellationToken.None);
            var active = await PrepareAsync(cancellationToken);
            if (!active.TryGetValue("php", out var php)) throw new InvalidOperationException(T("nginx.phpRequired", "Activate PHP before starting Nginx."));
            var settings = await Store.ReadAsync<AppSettings>(Paths.Resolve("app", "config", "settings.json"), cancellationToken) ?? new();
            await CheckPortsAsync(settings.HttpPort, cancellationToken);
            var hosts = await new VirtualHostStore(Paths, Store).ReadAsync(cancellationToken);
            if (settings.FastCgiPort == settings.HttpPort || hosts.Any(h => h.Enabled && (h.Port ?? settings.HttpPort) == settings.FastCgiPort))
                throw new InvalidOperationException(T("nginx.portConflict", "FastCGI and HTTP must use different ports."));
            CheckPort(settings.FastCgiPort, IPAddress.Loopback);
            var version = active[Id];
            var root = Paths.Resolve("bin", Id, version);
            var config = Path.Combine(VersionedComponentConfiguration.GetGeneratedDirectory(Paths, Id, version), "nginx.conf");
            _definition = new(Id, Path.Combine(root, "nginx.exe"),
                $"-p \"{root.Replace('\\', '/')}/\" -c \"{config.Replace('\\', '/')}\"",
                root, Paths.Resolve("app", "var", "logs", Id, "console.log"), KillChildrenOnExit: true);
            await RunToolAsync(_definition, "-t", cancellationToken);
            var phpRoot = Paths.Resolve("bin", "php", php);
            var env = PhpEnvironment(php);
            env["PHP_FCGI_MAX_REQUESTS"] = "0";
            await StartProcessAsync(new(FastCgiId, Path.Combine(phpRoot, "php-cgi.exe"),
                $"-c \"{PhpVersionConfiguration.GetIniPath(Paths, php)}\" -d opcache.enable=0 -b 127.0.0.1:{settings.FastCgiPort}", phpRoot,
                Paths.Resolve("app", "var", "logs", "php", "fastcgi.log"), env), cancellationToken);
            await WaitForPortAsync(FastCgiId, settings.FastCgiPort, cancellationToken, connect: false);
            await StartProcessAsync(_definition, cancellationToken);
            await WaitForPortAsync(Id, settings.HttpPort, cancellationToken, connect: true);
            _monitor = new();
            _ = MonitorAsync(_monitor.Token);
        }
        catch { await StopCoreAsync(CancellationToken.None); throw; }
        finally { _gate.Release(); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try { await StopCoreAsync(CancellationToken.None); }
        finally { _gate.Release(); }
    }

    private async Task StopCoreAsync(CancellationToken token)
    {
        _monitor?.Cancel();
        _monitor?.Dispose();
        _monitor = null;
        try
        {
            if (_definition is not null && Running(Id))
            {
                try
                {
                    await RunToolAsync(_definition, "-s quit", token);
                    for (var i = 0; i < 80 && Running(Id); i++) await Task.Delay(100, token);
                }
                catch (Exception ex) { Logs?.Write(Id, ex.Message, LogSeverity.Warning); }
            }
            await Processes.StopAsync(Id, TimeSpan.FromSeconds(8), token);
        }
        finally
        {
            // Windows php-cgi has no graceful control command. Drain Nginx first, then terminate only our child.
            await Processes.StopAsync(FastCgiId, TimeSpan.FromSeconds(8), CancellationToken.None);
            _definition = null;
        }
    }

    private async Task WaitForPortAsync(string id, int port, CancellationToken token, bool connect)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (Running(id))
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (!connect && IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port && IPAddress.IsLoopback(endpoint.Address))) return;
            try
            {
                if (!connect)
                {
                    await Task.Delay(100, timeout.Token);
                    continue;
                }
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
                return;
            }
            catch (SocketException) { await Task.Delay(100, timeout.Token); }
        }
        throw new InvalidOperationException(T("webserver.processExited", "Server process exited during startup."));
    }

    private async Task MonitorAsync(CancellationToken token)
    {
        try
        {
            while (true)
            {
                await Task.Delay(500, token);
                await _gate.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (Running(Id) && Running(FastCgiId)) continue;
                    Logs?.Write(Id, T("nginx.dependencyFailed", "Nginx or PHP FastCGI exited. Stopping both processes."), LogSeverity.Error);
                    await StopCoreAsync(CancellationToken.None);
                    return;
                }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Logs?.Write(Id, ex.Message, LogSeverity.Error); }
    }
}
