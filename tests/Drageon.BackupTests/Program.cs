using System.Text.RegularExpressions;
using Drageon.Core;
using Drageon.Infrastructure;

if (IsFakeTool()) return await RunFakeToolAsync();
if (args.FirstOrDefault() == "--live-file-exchange") return await LiveFileExchangeProbe.RunAsync(args);

var root = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine("work", "backup-smoke-root"));
var allowedRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work")) + Path.DirectorySeparatorChar;
if (!root.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test root must be a child of workspace/work.");
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

void Seed(string name, string sql) { var dir = paths.Resolve("fake-state"); Directory.CreateDirectory(dir); Directory.CreateDirectory(Path.Combine(dir, name)); File.WriteAllText(Path.Combine(dir, name + ".sql"), sql); }
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
    Check(Regex.IsMatch(Path.GetFileName(backup.Path), @"^maxspar_\d{4}-\d{2}-\d{2}_\d{6}_\d{3}\.sql$")
        && (await databases.GetBackupsAsync()).Any(item => item.Path == backup.Path && item.DatabaseName == "maxspar"), component + " short backup name retains seconds and database association");
    var backupDirectory = paths.Resolve("data", "backups", "databases");
    var beforeRestore = Directory.GetFiles(backupDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase);
    await databases.RestoreAsync(backup.Path, "maxspar"); Check(State("maxspar").Contains(component + " original"), component + " backup and restore");
    var restoreArtifacts = Directory.GetFiles(backupDirectory).Where(path => !beforeRestore.Contains(path)).ToArray();
    Check(restoreArtifacts.Length == 1 && Regex.IsMatch(Path.GetFileName(restoreArtifacts[0]), @"^maxspar_\d{4}-\d{2}-\d{2}_\d{6}_\d{3}-recovery\.sql$")
        && File.ReadAllText(restoreArtifacts[0]) == "-- changed", component + " successful restore retains only timestamped recovery backup of previous database");
    Check((await databases.GetBackupsAsync()).Any(item => item.Path == restoreArtifacts[0] && item.DatabaseName == "maxspar"), component + " recovery backup retains database association");
    await databases.DeleteBackupAsync(restoreArtifacts[0]);
    await databases.DeleteBackupAsync(backup.Path); Check(!File.Exists(backup.Path), component + " backup deletion");
    Check(!Directory.EnumerateFiles(paths.Resolve("app", "var", "tmp"), "db-client-*.ini").Any(), component + " temporary credential files removed");
}

await ConfigureFakeServer("mysql"); Seed("contao5", "-- project database");
var projectRoot = paths.Resolve("www", "contao5"); Directory.CreateDirectory(projectRoot); Directory.CreateDirectory(Path.Combine(projectRoot, "empty")); Directory.CreateDirectory(Path.Combine(projectRoot, "files"));
await File.WriteAllTextAsync(Path.Combine(projectRoot, "index.php"), "original"); await File.WriteAllTextAsync(Path.Combine(projectRoot, ".env"), "hidden-value");
var link = Path.Combine(projectRoot, "public-files");
SafeFileTree.CreateJunction(link, Path.Combine(projectRoot, "files"));
await projects.SetMetadataAsync("www/contao5", new ProjectMetadata { Favorite = true, Database = "contao5", Url = "http://contao5.localhost" });
await projects.SetNameAsync("www/contao5", "Contao Local");
Check((await projects.GetProjectsAsync()).Single(item => item.RelativePath == "www/contao5").Name == "Contao Local"
    && Directory.Exists(projectRoot), "project display name stored without renaming folder");
var snapshot = await snapshots.CreateProjectSnapshotAsync("www/contao5", "before update");
Check(snapshot.DirectoryPath.Replace('\\', '/').Contains("data/snapshots/contao5/", StringComparison.Ordinal)
    && File.Exists(Path.Combine(paths.Resolve(snapshot.DirectoryPath.Split('/')), "snapshot.json")), "project snapshot stored directly and completed in place");
Seed("contao5", "-- live changed database");
await File.WriteAllTextAsync(Path.Combine(projectRoot, "index.php"), "changed"); Directory.Delete(link); Directory.Delete(Path.Combine(projectRoot, "empty"));
await snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, backupCurrentState: true);
Check(State("contao5").Contains("project database"), "combined snapshot restores database");
Check((await snapshots.GetProjectSnapshotsAsync("www/contao5")).Count == 2, "combined snapshot creates optional current-state snapshot");
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
await snapshots.RestoreProjectSnapshotAsync(stagingSnapshot.DirectoryPath, false);
var stageRestoreJournal = Directory.EnumerateDirectories(paths.Resolve("data", "recovery"), "restore-*")
    .Select(directory => Path.Combine(directory, "restore.operation.json"))
    .Where(File.Exists).Single(file => System.Text.Json.JsonSerializer.Deserialize<ProjectRestoreState>(File.ReadAllText(file))?.Project == stagingResult.ProjectPath);
var stageRestoreState = System.Text.Json.JsonSerializer.Deserialize<ProjectRestoreState>(File.ReadAllText(stageRestoreJournal))!;
Check(stagingLink.ResolveLinkTarget(false)?.FullName.Equals(paths.Resolve("www", "contao5-staging", "files"), StringComparison.OrdinalIgnoreCase) == true, "ZIP staging snapshot restores internal junction");
Check(!Directory.EnumerateFileSystemEntries(paths.Resolve("www")).Any(path => Path.GetFileName(path).StartsWith("contao5-staging.", StringComparison.Ordinal)), "successful restore leaves no staging operation artifacts in www");
using (var archive = System.IO.Compression.ZipFile.OpenRead(paths.Resolve(stagingSnapshot.DirectoryPath.Split('/').Append("files.zip").ToArray())))
{
    var entry = archive.GetEntry("public-files")!;
    using var reader = new StreamReader(entry.Open());
    Check(reader.ReadToEnd() == "files" && (entry.ExternalAttributes & unchecked((int)0xF0000000)) == unchecked((int)0xA0000000), "ZIP explicitly contains internal junction target as link, without dereferencing it");
}
await staging.DeleteAsync(stagingResult.ProjectPath, true);
Check(!File.Exists(paths.Resolve("fake-state", "contao5_staging.sql")) && State("contao5").Contains("project database"), "owned staging database deleted and main database preserved");
Check(!Directory.Exists(paths.Resolve("www", "contao5-staging")), "staging deleted with owned database");
Check(!Directory.Exists(paths.Resolve("data", "snapshots", "contao5-staging")), "staging deletion removes snapshots");
Check(!Directory.Exists(stageRestoreState.Recovery) && !File.Exists(stageRestoreJournal), "staging deletion removes its completed combined restore artifacts");
foreach (var item in await snapshots.GetProjectSnapshotsAsync("www/contao5")) await snapshots.DeleteProjectSnapshotAsync(item.DirectoryPath); Check(!Directory.Exists(paths.Resolve("data", "snapshots", "contao5")), "last project snapshot removes project directory");

Directory.CreateDirectory(paths.Resolve("www", "broken-snapshot")); await projects.SetMetadataAsync("www/broken-snapshot", new ProjectMetadata { Database = "missing_database" });
try { await snapshots.CreateProjectSnapshotAsync("www/broken-snapshot", "expected failure"); } catch (InvalidOperationException) { }
Check(!Directory.Exists(paths.Resolve("data", "snapshots", "broken-snapshot")), "failed project snapshot removes incomplete and empty parent");

var orphan = paths.Resolve("www", "orphan"); Directory.CreateDirectory(orphan); await projects.SetMetadataAsync("www/orphan", new ProjectMetadata { Favorite = true }); Directory.Delete(orphan);
_ = await projects.GetProjectsAsync(); var metadataText = await File.ReadAllTextAsync(paths.Resolve("app", "config", "projects.json")); Check(metadataText.Contains("www/orphan"), "missing project metadata retained for interrupted operations");
Directory.CreateDirectory(paths.Resolve("www", "vhost-a")); Directory.CreateDirectory(paths.Resolve("www", "vhost-b")); var metadataStore = new ProjectMetadataStore(paths, store);
await metadataStore.AssignVirtualHostAsync(null, "www/vhost-a", "a.localhost", "a.localhost");
await metadataStore.AssignVirtualHostAsync("www/vhost-a", "www/vhost-b", "a.localhost", "b.localhost");
Check((await projects.GetMetadataAsync("www/vhost-a")) is null && (await projects.GetMetadataAsync("www/vhost-b"))?.Vhost == "b.localhost", "vhost rename and project reassignment metadata");
await metadataStore.AssignVirtualHostAsync("www/vhost-b", null, "b.localhost", "b.localhost"); Check((await projects.GetMetadataAsync("www/vhost-b")) is null, "vhost deletion removes empty metadata entry");
await WindowsSafetyRegression.RunAsync(paths, store, projects, databases, snapshots, staging, logs, Check);
await RestoreExchangeRegression.RunAsync(paths, store, projects, databases, snapshots, staging, logs, Check);
await CombinedSnapshotRegression.RunAsync(paths, store, projects, snapshots, Check);
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
    var state = Path.Combine(directory.FullName, "fake-state"); Directory.CreateDirectory(state); Directory.CreateDirectory(Path.Combine(state, "mysql"));
    if (exe.Contains("dump"))
    {
        if (File.Exists(Path.Combine(state, "fail-dump"))) { Console.Error.Write("Injected dump failure"); return 3; }
        var database = args.Last();
        if (database.StartsWith('-') && !args[..^1].Contains("--")) return 4;
        var file = Path.Combine(state, database + ".sql");
        if (!File.Exists(file)) return 2; Console.Write(await File.ReadAllTextAsync(file)); return 0;
    }
    var executeIndex = Array.FindIndex(args, item => item == "--execute");
    if (executeIndex >= 0)
    {
        var sql = args[executeIndex + 1];
        if (sql.Contains("SHOW DATABASES", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("information_schema"); Console.WriteLine("mysql"); foreach (var file in Directory.EnumerateFiles(state, "*.sql")) Console.WriteLine(Path.GetFileNameWithoutExtension(file)); return 0; }
        if (sql.Contains("DEFAULT_CHARACTER_SET_NAME", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("utf8mb4\tutf8mb4_unicode_ci"); return 0; }
        if (sql.Contains("@@hostname", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"fake-host\t{state}\t{args.FirstOrDefault(item => item.StartsWith("--port="))}\t1\tfake-version"); return 0; }
        if (sql.Contains("@@server_uuid", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine("fake-uuid-" + state); return 0; }
        if (sql.Contains("@@datadir", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine(state); return 0; }
        foreach (Match match in Regex.Matches(sql, @"DROP DATABASE (?:IF EXISTS )?`([^`]+)`", RegexOptions.IgnoreCase))
        {
            File.Delete(Path.Combine(state, match.Groups[1].Value + ".sql"));
            var schema = Path.Combine(state, match.Groups[1].Value);
            if (Directory.Exists(schema)) Directory.Delete(schema);
        }
        foreach (Match match in Regex.Matches(sql, @"CREATE DATABASE `([^`]+)`", RegexOptions.IgnoreCase))
        {
            var path = Path.Combine(state, match.Groups[1].Value + ".sql");
            if (File.Exists(path)) { Console.Error.Write("Database exists"); return 2; }
            File.WriteAllText(path, "");
            Directory.CreateDirectory(Path.Combine(state, match.Groups[1].Value));
        }
        return 0;
    }
    var target = args.Last();
    if (target.StartsWith('-') && !args[..^1].Contains("--")) return 4;
    var input = await Console.In.ReadToEndAsync();
    await File.WriteAllTextAsync(Path.Combine(state, target + ".sql"), input);
    if (input.Contains("SLOW_IMPORT")) { File.WriteAllText(Path.Combine(state, "import-started"), ""); await Task.Delay(30000); }
    if (input.Contains("FAIL_IMPORT") || File.Exists(Path.Combine(state, "fail-all-imports"))) { Console.Error.Write("Injected import error"); return 3; }
    return 0;
}
