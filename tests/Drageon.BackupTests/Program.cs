using System.Text.RegularExpressions;
using Drageon.Core;
using Drageon.Infrastructure;

if (IsFakeTool()) return await RunFakeToolAsync();

var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("work", "backup-smoke-root"));
if (Directory.Exists(root)) DeleteTree(root);
var paths = new PortablePathService(root); paths.EnsureDirectoryLayout();
var store = new JsonFileStore(); var logs = new AppLogService(paths); var settings = new SettingsService(paths, store); await settings.LoadAsync();
var projects = new ProjectDiscoveryService(paths, store, settings);
var databases = new DatabaseBackupService(paths, store, logs);
var snapshots = new ProjectSnapshotService(paths, store, projects, databases, logs);
var staging = new StagingService(paths, store, projects, databases, snapshots, logs);
var checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); checks++; }

async Task ConfigureFakeServer(string component)
{
    const string version = "test";
    await store.WriteAsync(paths.Resolve("app", "config", "packages.active.json"), new Dictionary<string, string> { [component] = version });
    var bin = paths.Resolve("bin", component, version, "bin"); Directory.CreateDirectory(bin);
    var self = Environment.ProcessPath!;
    foreach (var name in component == "mysql" ? new[] { "mysql.exe", "mysqldump.exe" } : new[] { "mariadb.exe", "mariadb-dump.exe" }) File.Copy(self, Path.Combine(bin, name), true);
    var assembly = System.Reflection.Assembly.GetExecutingAssembly().Location;
    foreach (var companion in new[] { assembly, Path.ChangeExtension(assembly, ".deps.json"), Path.ChangeExtension(assembly, ".runtimeconfig.json") })
        File.Copy(companion, Path.Combine(bin, Path.GetFileName(companion)), true);
    foreach (var dependency in Directory.EnumerateFiles(Path.GetDirectoryName(assembly)!, "*.dll"))
        File.Copy(dependency, Path.Combine(bin, Path.GetFileName(dependency)), true);
    await store.WriteAsync(VersionedComponentConfiguration.GetSettingsPath(paths, component, version), component == "mysql" ? (object)new MySqlSettings() : new MariaDbSettings());
}

void Seed(string name, string sql) { var dir = paths.Resolve("fake-state"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, name + ".sql"), sql); }
string State(string name) => File.ReadAllText(paths.Resolve("fake-state", name + ".sql"));

foreach (var component in new[] { "mysql", "mariadb" })
{
    await ConfigureFakeServer(component); Seed("maxspar", $"-- {component} original");
    var protectedPassword = DatabaseCredentialProtector.Protect("test-secret", paths);
    Check(DatabaseCredentialProtector.IsPortable(protectedPassword), component + " uses portable credential encryption");
    var portableCopyPaths = new PortablePathService(paths.Resolve("portable-copy")); portableCopyPaths.EnsureDirectoryLayout();
    File.Copy(paths.Resolve("app", "config", "database-credentials.key"), portableCopyPaths.Resolve("app", "config", "database-credentials.key"), true);
    Check(DatabaseCredentialProtector.Unprotect(protectedPassword, portableCopyPaths) == "test-secret", component + " credentials survive a portable directory copy");
    if (component == "mysql") await store.WriteAsync(VersionedComponentConfiguration.GetSettingsPath(paths, component, "test"), new MySqlSettings { DatabaseUser = "root", ProtectedPassword = protectedPassword });
    else await store.WriteAsync(VersionedComponentConfiguration.GetSettingsPath(paths, component, "test"), new MariaDbSettings { DatabaseUser = "root", ProtectedPassword = protectedPassword });
    Check((await databases.GetActiveServerAsync()).Connection.Password == "test-secret", component + " encrypted component credentials loaded");
    var listed = await databases.GetDatabasesAsync(); Check(listed.SequenceEqual(new[] { "maxspar" }), component + " lists only user databases");
    var backup = (await databases.BackupAsync(["maxspar"])).Single(); Seed("maxspar", "-- changed");
    await databases.RestoreAsync(backup.Path, "maxspar"); Check(State("maxspar").Contains(component + " original"), component + " backup and restore");
    await databases.DeleteBackupAsync(backup.Path); Check(!File.Exists(backup.Path), component + " backup deletion");
    Check(!Directory.EnumerateFiles(paths.Resolve("app", "var", "tmp"), "db-client-*.ini").Any(), component + " temporary credential files removed");
}

await ConfigureFakeServer("mysql"); Seed("contao5", "-- project database");
var projectRoot = paths.Resolve("www", "contao5"); Directory.CreateDirectory(projectRoot); Directory.CreateDirectory(Path.Combine(projectRoot, "empty")); Directory.CreateDirectory(Path.Combine(projectRoot, "files"));
await File.WriteAllTextAsync(Path.Combine(projectRoot, "index.php"), "original"); await File.WriteAllTextAsync(Path.Combine(projectRoot, ".env"), "hidden-value");
var link = Path.Combine(projectRoot, "public-files");
var junctionInfo = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, Path.Combine(projectRoot, "files") }) junctionInfo.ArgumentList.Add(argument);
using (var junction = System.Diagnostics.Process.Start(junctionInfo)!) { await junction.WaitForExitAsync(); if (junction.ExitCode != 0) throw new Exception("Test junction could not be created."); }
await projects.SetMetadataAsync("www/contao5", new ProjectMetadata { Favorite = true, Database = "contao5", Url = "http://contao5.localhost" });
await projects.SetNameAsync("www/contao5", "Contao Local");
Check((await projects.GetProjectsAsync()).Single(item => item.RelativePath == "www/contao5").Name == "Contao Local"
    && Directory.Exists(projectRoot), "project display name stored without renaming folder");
var snapshot = await snapshots.CreateProjectSnapshotAsync("www/contao5", "before update");
Check(snapshot.DirectoryPath.Replace('\\', '/').Contains("data/snapshots/contao5/", StringComparison.Ordinal)
    && File.Exists(Path.Combine(paths.Resolve(snapshot.DirectoryPath.Split('/')), "snapshot.json")), "project snapshot stored directly and completed in place");
await File.WriteAllTextAsync(Path.Combine(projectRoot, "index.php"), "changed"); Directory.Delete(link); Directory.Delete(Path.Combine(projectRoot, "empty"));
await snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, backupCurrentState: false);
Check(await File.ReadAllTextAsync(Path.Combine(projectRoot, "index.php")) == "original", "project file restore");
Check(await File.ReadAllTextAsync(Path.Combine(projectRoot, ".env")) == "hidden-value", "dotfile restore");
Check(Directory.Exists(Path.Combine(projectRoot, "empty")), "empty directory restore");
Check(new DirectoryInfo(link).LinkTarget is not null, "directory junction restored without dereferencing");

await projects.SetVirtualHostAsync("www/contao5", "contao5.localhost");
await store.WriteAsync(paths.Resolve("app", "config", "vhosts", "contao5.localhost.json"), new VirtualHostDefinition("contao5.localhost", "www/contao5"));
var stagingResult = await staging.CreateAsync(new("www/contao5", "Contao 5 – Staging", "www/contao5-staging", "http://contao5-staging.localhost", "contao5_staging", true, true));
var stagingLink = new DirectoryInfo(paths.Resolve("www", "contao5-staging", "public-files"));
Check(Directory.Exists(paths.Resolve("www", "contao5-staging")) && stagingLink.LinkTarget is not null, "staging copies files and link");
Check(stagingLink.ResolveLinkTarget(false)?.FullName.Equals(paths.Resolve("www", "contao5-staging", "files"), StringComparison.OrdinalIgnoreCase) == true, "staging internal link points to staging target");
Check(State("contao5_staging").Contains("project database"), "staging database copied");
Check((await projects.GetMetadataAsync(stagingResult.ProjectPath))?.StagingOf == "www/contao5", "staging metadata stored");
var stagingVhost = await store.ReadAsync<VirtualHostDefinition>(paths.Resolve("app", "config", "vhosts", "contao5-staging.localhost.json"));
Check(stagingVhost?.DocumentRoot == "www/contao5-staging", "staging vhost document root has no dot segment");
var stagingSnapshot = await snapshots.CreateProjectSnapshotAsync(stagingResult.ProjectPath, "staging state");
Check(Directory.Exists(paths.Resolve(stagingSnapshot.DirectoryPath.Split('/'))), "staging snapshot created");
await staging.DeleteAsync(stagingResult.ProjectPath, true); Check(!Directory.Exists(paths.Resolve("www", "contao5-staging")), "staging deleted");
Check(!Directory.Exists(paths.Resolve("data", "snapshots", "contao5-staging")), "staging deletion removes snapshots");
await snapshots.DeleteProjectSnapshotAsync(snapshot.DirectoryPath); Check(!Directory.Exists(paths.Resolve("data", "snapshots", "contao5")), "last project snapshot removes project directory");

Directory.CreateDirectory(paths.Resolve("www", "broken-snapshot")); await projects.SetMetadataAsync("www/broken-snapshot", new ProjectMetadata { Database = "missing_database" });
try { await snapshots.CreateProjectSnapshotAsync("www/broken-snapshot", "expected failure"); } catch (InvalidOperationException) { }
Check(!Directory.Exists(paths.Resolve("data", "snapshots", "broken-snapshot")), "failed project snapshot removes incomplete and empty parent");

var orphan = paths.Resolve("www", "orphan"); Directory.CreateDirectory(orphan); await projects.SetMetadataAsync("www/orphan", new ProjectMetadata { Favorite = true }); Directory.Delete(orphan);
_ = await projects.GetProjectsAsync(); var metadataText = await File.ReadAllTextAsync(paths.Resolve("app", "config", "projects.json")); Check(!metadataText.Contains("www/orphan"), "orphan metadata pruned on project scan");
Directory.CreateDirectory(paths.Resolve("www", "vhost-a")); Directory.CreateDirectory(paths.Resolve("www", "vhost-b")); var metadataStore = new ProjectMetadataStore(paths, store);
await metadataStore.AssignVirtualHostAsync(null, "www/vhost-a", "a.localhost", "a.localhost");
await metadataStore.AssignVirtualHostAsync("www/vhost-a", "www/vhost-b", "a.localhost", "b.localhost");
Check((await projects.GetMetadataAsync("www/vhost-a")) is null && (await projects.GetMetadataAsync("www/vhost-b"))?.Vhost == "b.localhost", "vhost rename and project reassignment metadata");
await metadataStore.AssignVirtualHostAsync("www/vhost-b", null, "b.localhost", "b.localhost"); Check((await projects.GetMetadataAsync("www/vhost-b")) is null, "vhost deletion removes empty metadata entry");
Console.WriteLine($"PASS: {checks} backup/snapshot/staging checks");
return 0;

bool IsFakeTool() => Path.GetFileName(Environment.ProcessPath!).ToLowerInvariant() is "mysql.exe" or "mysqldump.exe" or "mariadb.exe" or "mariadb-dump.exe";
void DeleteTree(string path)
{
    var directory = new DirectoryInfo(path);
    foreach (var item in directory.EnumerateFileSystemInfos())
    {
        if (item.LinkTarget is not null) item.Delete();
        else if (item is DirectoryInfo child) DeleteTree(child.FullName);
        else { item.Attributes = FileAttributes.Normal; item.Delete(); }
    }
    directory.Delete();
}
async Task<int> RunFakeToolAsync()
{
    var exe = Path.GetFileName(Environment.ProcessPath!).ToLowerInvariant(); var directory = new DirectoryInfo(Path.GetDirectoryName(Environment.ProcessPath!)!);
    for (var i = 0; i < 4; i++) directory = directory.Parent!;
    var state = Path.Combine(directory.FullName, "fake-state"); Directory.CreateDirectory(state);
    if (exe.Contains("dump"))
    {
        var database = args.Last(item => !item.StartsWith('-')); var file = Path.Combine(state, database + ".sql");
        if (!File.Exists(file)) return 2; Console.Write(await File.ReadAllTextAsync(file)); return 0;
    }
    var executeIndex = Array.FindIndex(args, item => item == "--execute");
    if (executeIndex >= 0)
    {
        var sql = args[executeIndex + 1];
        if (sql.Contains("SHOW DATABASES", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("information_schema"); Console.WriteLine("mysql"); foreach (var file in Directory.EnumerateFiles(state, "*.sql")) Console.WriteLine(Path.GetFileNameWithoutExtension(file)); return 0; }
        if (sql.Contains("DEFAULT_CHARACTER_SET_NAME", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("utf8mb4\tutf8mb4_unicode_ci"); return 0; }
        foreach (Match match in Regex.Matches(sql, @"DROP DATABASE IF EXISTS `([^`]+)`", RegexOptions.IgnoreCase)) File.Delete(Path.Combine(state, match.Groups[1].Value + ".sql"));
        foreach (Match match in Regex.Matches(sql, @"CREATE DATABASE `([^`]+)`", RegexOptions.IgnoreCase)) File.WriteAllText(Path.Combine(state, match.Groups[1].Value + ".sql"), "");
        return 0;
    }
    var target = args.Last(item => !item.StartsWith('-')); await File.WriteAllTextAsync(Path.Combine(state, target + ".sql"), await Console.In.ReadToEndAsync()); return 0;
}
