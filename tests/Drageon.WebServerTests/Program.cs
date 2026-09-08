using System.Diagnostics;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Drageon.Core;
using Drageon.Infrastructure;

var root = Path.GetFullPath(args.Length > 0 ? args[0] : "work/nginx-smoke-root");
var archive = Path.GetFullPath(args.Length > 1 ? args[1] : "work/nginx-1.31.5.zip");
if (!File.Exists(Path.Combine(root, ".webserver-test-root"))) throw new InvalidOperationException("Run scripts/test-webservers.ps1 to create an isolated test root.");
var paths = new PortablePathService(root);
paths.EnsureDirectoryLayout();
var store = new JsonFileStore();
var settings = new SettingsService(paths, store);
await settings.LoadAsync();
var logs = new AppLogService(paths);
logs.EntryAdded += (_, entry) => Console.WriteLine($"[{entry.Component}] {entry.Message}");
await using var processes = new ProcessManager(logs);
IConfigGenerator[] generators = [new ApacheConfigGenerator(paths, store), new NginxConfigGenerator(paths, store), new PhpConfigGenerator(paths, store), new PhpMyAdminConfigGenerator(paths, store)];
var generatorsById = generators.ToDictionary(generator => generator.ComponentId);
var webServers = new WebServerRegistry([
    new ApacheWebServer(paths, store, processes, generatorsById, logs),
    new NginxWebServer(paths, store, processes, generatorsById, logs)
]);
var runtime = new RuntimeController(paths, store, processes, generators, webServers, logs);
var packageManager = new PackageManager(paths, store, httpClient: new HttpClient(new ArchiveHandler(archive)));
var manifest = (await packageManager.GetManifestsAsync()).Single(m => m.Id == "nginx");
if (!(await packageManager.GetInstalledAsync()).Any(p => p.Id == "nginx")) await packageManager.InstallAsync(manifest, manifest.Releases[0]);
var active = new Dictionary<string, string> { ["apache"] = "2.4.68", ["php"] = "8.5.10", ["nginx"] = manifest.Releases[0].Version };
await store.WriteAsync(paths.Resolve("app", "config", "packages.active.json"), active);
var http = FreePort(); var fcgi = FreePort(); var vhostPort = FreePort();
await settings.SaveAsync(settings.Current with { WebServer = "nginx", HttpPort = http, FastCgiPort = fcgi });
await store.WriteAsync(VersionedComponentConfiguration.GetSettingsPath(paths, "apache", active["apache"]), new ApacheSettings());
Directory.CreateDirectory(paths.Resolve("www", "projekt"));
Directory.CreateDirectory(paths.Resolve("www", "contao5", "public"));
await File.WriteAllTextAsync(paths.Resolve("www", "projekt", "index.php"), "<?php echo 'folder:' . PHP_SAPI . ':' . $_GET['q'];");
await File.WriteAllTextAsync(paths.Resolve("www", "opcache.php"), "<?php echo ini_get('opcache.enable');");
await File.WriteAllTextAsync(paths.Resolve("www", "contao5", "public", "index.php"), "<?php echo 'vhost:' . $_SERVER['REQUEST_URI'];");
await File.WriteAllTextAsync(paths.Resolve("www", "contao5", "public", "asset.txt"), "static-content");
await store.WriteAsync(paths.Resolve("app", "config", "vhosts", "contao.json"),
    new VirtualHostDefinition("contao5.localhost", "www/contao5/public", Port: vhostPort)
    {
        ServerConfiguration = new Dictionary<string, JsonElement>
        {
            ["apache"] = JsonSerializer.SerializeToElement("SetEnv VHOST_TEST apache"),
            ["nginx"] = JsonSerializer.SerializeToElement("add_header X-VHost-Test nginx;")
        }
    });
using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
int count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); count++; }
bool Running(string id) => processes.GetStatuses().Any(s => s.Id == id && s.State == ComponentState.Running);
async Task<string> Get(string path, int? port = null, string? host = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port ?? http}/{path}");
    request.Headers.Host = host ?? $"localhost:{port ?? http}";
    using var response = await client.SendAsync(request);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadAsStringAsync();
}
async Task ExpectFailure(Func<Task> action, string name)
{
    try { await action(); } catch { Check(true, name); return; }
    throw new Exception("FAIL: expected failure: " + name);
}
async Task WaitStopped()
{
    for (int i = 0; i < 150 && (Running("nginx") || Running(NginxWebServer.FastCgiId)); i++) await Task.Delay(100);
    Check(!Running("nginx") && !Running(NginxWebServer.FastCgiId), "paired cleanup");
}
try
{
    var packageManifests = await packageManager.GetManifestsAsync();
    Check(packageManifests.Count == 9 && packageManifests.All(item => item.Bundled), "all bundled package definitions are present");
    Check(packageManifests.Count(item => item.Category == "webserver") == 2
        && packageManifests.Count(item => item.Category == "php") == 2
        && packageManifests.Count(item => item.Category == "database") == 2
        && packageManifests.Count(item => item.Category == "developmentTool") == 3, "package categories");
    Check(packageManifests.Where(item => item.Category == "database").All(item => item.ExclusiveGroup == "database"), "database exclusive group metadata");
    await ExpectFailure(() => packageManager.DeleteComponentAsync("curl"), "bundled package definition protected");
    Directory.CreateDirectory(paths.Resolve("bin", "mariadb", "group-test"));
    Directory.CreateDirectory(paths.Resolve("bin", "mysql", "group-test"));
    await packageManager.ActivateAsync("mariadb", "group-test");
    await packageManager.ActivateAsync("mysql", "group-test");
    var exclusiveActive = (await packageManager.GetInstalledAsync()).Where(item => item.IsActive).Select(item => item.Id).ToArray();
    Check(exclusiveActive.Contains("mysql") && !exclusiveActive.Contains("mariadb"), "manifest-driven exclusive activation");
    await store.WriteAsync(paths.Resolve("app", "config", "packages.active.json"), active);
    await runtime.StartAllAsync();
    Check(Running("nginx") && Running(NginxWebServer.FastCgiId) && !Running("apache"), "selected server + FastCGI started");
    await Task.Delay(5000);
    Check(Running("nginx") && Running(NginxWebServer.FastCgiId), "FastCGI stays alive after readiness check");
    Check(await Get("opcache.php") == "0", "OPcache disabled for Windows FastCGI process");
    var generatedNginx = await File.ReadAllTextAsync(await generatorsById["nginx"].GenerateAsync());
    Check(generatedNginx.Contains("add_header X-VHost-Test nginx;") && !generatedNginx.Contains("SetEnv VHOST_TEST"), "Nginx-only vhost configuration");
    Check((await Get("projekt/?q=test")).Contains("folder:cgi-fcgi:test"), "localhost subdirectory PHP and query string");
    Check((await Get("some/route?q=1", vhostPort, "contao5.localhost")).Contains("vhost:/some/route?q=1"), "vhost + front controller + separate port");
    Check(await Get("asset.txt", vhostPort, "contao5.localhost") == "static-content", "static vhost file");
    using (var missing = await client.GetAsync($"http://localhost:{http}/missing.php")) Check(missing.StatusCode == HttpStatusCode.NotFound, "missing PHP returns 404");
    var page = await Get("");
    Check(page.Contains("nginx", StringComparison.OrdinalIgnoreCase) && page.Contains($"127.0.0.1:{fcgi}"), "start page displays Nginx + FastCGI endpoint");
    Check(page.Contains($"http://contao5.localhost:{vhostPort}/"), "start page uses virtual host project link");
    await ExpectFailure(() => runtime.StartAsync("apache"), "unselected Apache cannot start");
    var originalPid = processes.GetStatuses().Single(s => s.Id == "nginx").ProcessId;
    await Task.WhenAll(runtime.StartAsync("nginx"), runtime.StartAsync("nginx"));
    Check(originalPid == processes.GetStatuses().Single(s => s.Id == "nginx").ProcessId, "concurrent start is idempotent");
    await runtime.StopAsync("nginx");
    await WaitStopped();
    await runtime.StartAsync("nginx");
    Check((await Get("projekt/?q=restart")).Contains("restart"), "restart serves PHP");
    using (var php = Process.GetProcessById(processes.GetStatuses().Single(s => s.Id == NginxWebServer.FastCgiId).ProcessId!.Value)) php.Kill(true);
    await WaitStopped();
    await runtime.StartAsync("nginx");
    using (var nginx = Process.GetProcessById(processes.GetStatuses().Single(s => s.Id == "nginx").ProcessId!.Value)) nginx.Kill();
    await WaitStopped();
    await runtime.StopAllAsync();
    fcgi = FreePort();
    await settings.SaveAsync(settings.Current with { FastCgiPort = fcgi });
    using (var busy = new TcpListener(IPAddress.Loopback, fcgi))
    {
        busy.Start();
        await ExpectFailure(() => runtime.StartAsync("nginx"), "occupied FastCGI port rejected");
        Check(!Running("nginx") && !Running(NginxWebServer.FastCgiId), "port failure leaves no processes");
    }
    http = FreePort();
    await settings.SaveAsync(settings.Current with { HttpPort = http });
    await store.WriteAsync(VersionedComponentConfiguration.GetSettingsPath(paths, "apache", active["apache"]), new ApacheSettings());
    using (var busy = new TcpListener(IPAddress.Any, http))
    {
        busy.Start();
        await ExpectFailure(() => runtime.StartAsync("nginx"), "occupied HTTP port rejected");
    }
    active.Remove("php");
    await store.WriteAsync(paths.Resolve("app", "config", "packages.active.json"), active);
    await ExpectFailure(() => runtime.StartAsync("nginx"), "missing active PHP rejected");
    active["php"] = "8.5.10";
    await store.WriteAsync(paths.Resolve("app", "config", "packages.active.json"), active);
    await settings.SaveAsync(settings.Current with { FastCgiPort = http });
    await ExpectFailure(() => runtime.StartAsync("nginx"), "HTTP/FastCGI collision rejected");
    await settings.SaveAsync(settings.Current with { WebServer = "apache", FastCgiPort = fcgi });
    await runtime.StartAllAsync();
    Check(Running("apache") && !Running("nginx"), "Apache remains available");
    Check((await Get("projekt/?q=apache")).Contains("folder:apache2handler:apache"), "Apache PHP module regression");
    Check((await Get("", vhostPort, "contao5.localhost")).Contains("vhost:/"), "Apache renders shared vhost");
    var generatedApache = await File.ReadAllTextAsync(await generatorsById["apache"].GenerateAsync());
    Check(generatedApache.Contains("SetEnv VHOST_TEST apache") && !generatedApache.Contains("add_header X-VHost-Test"), "Apache-only vhost configuration");
    Check((await Get("")).Contains("Apache"), "start page displays Apache");
    await settings.SaveAsync(settings.Current with { WebServer = "nginx" });
    await runtime.StartAsync("nginx");
    Check(!Running("apache") && Running("nginx"), "switch stops Apache before Nginx");
    Check((await new ProjectDiscoveryService(paths, store, settings).GetProjectsAsync()).Any(p => p.Name == "contao5" && p.Url == $"http://contao5.localhost:{vhostPort}/"), "shared project discovery");
    await runtime.StopAllAsync();
    await WaitStopped();
    Check(File.Exists(paths.Resolve("app", "var", "logs", "nginx", "access.log")), "access log generated");
    // Exercise the external phpMyAdmin alias without depending on a database server.
    active["phpmyadmin"] = "alias-test";
    var pma = paths.Resolve("bin", "phpmyadmin", "alias-test");
    Directory.CreateDirectory(paths.Resolve("bin", "phpmyadmin", "other-installed-version"));
    Directory.CreateDirectory(pma);
    await File.WriteAllTextAsync(Path.Combine(pma, "index.php"), "<?php echo 'pma:' . $_GET['q'] . ':' . $_SERVER['SCRIPT_NAME'] . ':' . $_SERVER['HTTPS'];");
    await File.WriteAllTextAsync(Path.Combine(pma, "style.css"), "body{}");
    await store.WriteAsync(paths.Resolve("app", "config", "packages.active.json"), active);
    await runtime.StartAsync("nginx");
    Check((await Get("")).Contains("alias-test"), "start page selects active phpMyAdmin version");
    Check((await Get("phpmyadmin/?q=alias")).Contains("pma:alias:/phpmyadmin/index.php:off"), "phpMyAdmin alias preserves cookie-relevant request metadata");
    Check(await Get("phpmyadmin/style.css") == "body{}", "phpMyAdmin alias serves static files");
    await runtime.StopAsync("nginx");
    var secondPhp = paths.Resolve("bin", "php", "8.5.10-test");
    var originalPhp = paths.Resolve("bin", "php", "8.5.10");
    if (!Directory.Exists(secondPhp))
    {
        foreach (var file in Directory.EnumerateFiles(originalPhp, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(secondPhp, Path.GetRelativePath(originalPhp, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
    active["php"] = "8.5.10-test";
    await store.WriteAsync(paths.Resolve("app", "config", "packages.active.json"), active);
    await store.WriteAsync(VersionedComponentConfiguration.GetSettingsPath(paths, "php", "8.5.10-test"), new PhpSettings { MemoryLimit = "192M", CustomIni = "max_input_vars=1234" });
    await File.WriteAllTextAsync(paths.Resolve("www", "php-config.php"), "<?php echo php_ini_loaded_file() . ':' . ini_get('memory_limit') . ':' . ini_get('max_input_vars');");
    await runtime.StartAsync("nginx");
    var phpConfig = await Get("php-config.php");
    Check(phpConfig.Contains("8.5.10-test") && phpConfig.Contains(":192M:1234"), "active PHP version uses its generated INI");
    await runtime.StopAllAsync();
    using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
    await ExpectFailure(() => runtime.StartAsync("nginx", cancelled.Token), "cancelled startup");
    await WaitStopped();
    Check(!Process.GetProcessesByName("nginx").Any(process => IsProcessUnderRoot(process, root)), "no orphan Nginx workers");
    Console.WriteLine($"ALL {count} CHECKS PASSED");
}
finally { await runtime.StopAllAsync(); }

static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
static bool IsProcessUnderRoot(Process process, string root)
{
    try { return process.MainModule?.FileName?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true; }
    catch (Win32Exception) { return false; }
    finally { process.Dispose(); }
}
sealed class ArchiveHandler(string archive) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(archive)) });
}



