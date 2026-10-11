using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Drageon.Core;
using Drageon.Infrastructure;

internal static class CombinedSnapshotRegression
{
    internal static async Task RunAsync(PortablePathService paths, JsonFileStore store, ProjectDiscoveryService projects,
        ProjectSnapshotService snapshots, Action<bool, string> check)
    {
        void Seed(string name, string sql)
        {
            Directory.CreateDirectory(paths.Resolve("fake-state", name));
            File.WriteAllText(paths.Resolve("fake-state", name + ".sql"), sql);
        }
        string Sql(string name) => File.ReadAllText(paths.Resolve("fake-state", name + ".sql"));
        async Task<RecoveryOperationException> Fails(Func<Task> operation, RecoveryOutcome outcome, string name)
        {
            RecoveryOperationException? failure = null;
            try { await operation(); } catch (RecoveryOperationException exception) { failure = exception; }
            check(failure?.Outcome == outcome, name + " outcome");
            return failure!;
        }
        // Successful restores archive their mandatory fallback; interrupted archival remains recoverable.
        foreach (var interruption in new string?[] { null, "snapshot-archive-prepared", "snapshot-archive-moved" })
        {
            var archiveProject = "www/archive-" + (interruption ?? "success");
            var archiveRoot = paths.Resolve(archiveProject.Split('/')); Directory.CreateDirectory(archiveRoot);
            var archiveFile = Path.Combine(archiveRoot, "state.txt"); File.WriteAllText(archiveFile, "snapshot");
            var archiveSnapshot = await snapshots.CreateProjectSnapshotAsync(archiveProject, "archive cleanup");
            File.WriteAllText(archiveFile, "before restore");
            try
            {
                OperationSafety.Checkpoint = step => { if (step == interruption) throw new IOException("Injected archival failure"); };
                if (interruption is null) await snapshots.RestoreProjectSnapshotAsync(archiveSnapshot.DirectoryPath, false, expectedProjectPath: archiveProject);
                else await Fails(() => snapshots.RestoreProjectSnapshotAsync(archiveSnapshot.DirectoryPath, false, expectedProjectPath: archiveProject), RecoveryOutcome.Completed, interruption);
            }
            finally { OperationSafety.Checkpoint = null; }
            check(File.ReadAllText(archiveFile) == "snapshot", "archival outcome preserves completed restore " + interruption);
            var saved = Directory.EnumerateDirectories(paths.Resolve("data", "recovery"), "restore-*")
                .Select(directory => Path.Combine(directory, "restore.operation.json")).Where(File.Exists)
                .Single(path => JsonSerializer.Deserialize<ProjectRestoreState>(File.ReadAllText(path))?.Project == archiveProject);
            if (interruption is null)
            {
                check(!Directory.EnumerateFileSystemEntries(paths.Resolve("www")).Any(path => Path.GetFileName(path).StartsWith(Path.GetFileName(archiveRoot) + ".", StringComparison.Ordinal)), "successful restore cleans www without optional snapshot");
                check(File.ReadAllText(Path.Combine(Path.GetDirectoryName(saved)!, "files.before", "state.txt")) == "before restore", "mandatory file fallback retained outside www");
            }
            await snapshots.RecoverProjectRestoreAsync(saved, archiveProject);
            check(File.ReadAllText(archiveFile) == "before restore", "archived or interrupted archive journal restores previous files " + interruption);
        }
        const string project = "www/combined-restore";
        var root = paths.Resolve(project.Split('/')); Directory.CreateDirectory(root);
        var file = Path.Combine(root, "index.txt"); File.WriteAllText(file, "snapshot files");
        var originalTime = new DateTime(2026, 1, 20, 12, 34, 56, 123, DateTimeKind.Utc).AddTicks(4567);
        File.SetLastWriteTimeUtc(file, originalTime);
        var hostPath = paths.Resolve("app", "config", "vhosts", "combined.localhost.json");
        var originalHost = new VirtualHostDefinition("combined.localhost", project);
        await store.WriteAsync(hostPath, originalHost);
        var originalMetadata = new ProjectMetadata { Name = "Original", Database = "restore_a", Vhost = originalHost.Host };
        Seed("restore_a", "-- snapshot A"); Seed("restore_b", "-- live B");
        await projects.SetMetadataAsync(project, originalMetadata);
        var snapshot = await snapshots.CreateProjectSnapshotAsync(project, "combined");
        File.WriteAllText(file, "live files"); Seed("restore_a", "-- live A");
        await projects.SetMetadataAsync(project, originalMetadata with { Name = "Changed", Database = "restore_b" });
        await store.WriteAsync(hostPath, originalHost with { Port = 8080 });
        await snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, true, expectedProjectPath: project);
        check(Sql("restore_a") == "-- snapshot A" && Sql("restore_b") == "-- live B", "snapshot restores historical database A and preserves current assignment B");
        check(File.ReadAllText(file) == "snapshot files" && await projects.GetMetadataAsync(project) == originalMetadata, "combined restore installs files and metadata");
        check(File.GetLastWriteTimeUtc(file) == originalTime, "ZIP restore preserves precise UTC file timestamp independently of DOS ZIP time");
        check(JsonSerializer.Serialize(await store.ReadAsync<VirtualHostDefinition>(hostPath)) == JsonSerializer.Serialize(originalHost), "combined restore restores own changed VHost");
        var currentSnapshot = (await snapshots.GetProjectSnapshotsAsync(project)).Single(item => item.DirectoryPath != snapshot.DirectoryPath);
        check(File.ReadAllText(paths.Resolve(currentSnapshot.DirectoryPath.Split('/').Append("database.sql").ToArray())) == "-- live B", "optional current snapshot backs up actual current assignment B");
        check(Directory.EnumerateFiles(paths.Resolve("data", "recovery"), "database.before.sql", SearchOption.AllDirectories).Any(path => File.ReadAllText(path) == "-- live A"), "mandatory recovery dump independently preserves target A");

        // The same safe restore is supported when the project directory is absent.
        SafeFileTree.Delete(root); Seed("restore_a", "-- absent project live A");
        await snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, false, expectedProjectPath: project);
        check(File.ReadAllText(file) == "snapshot files" && Sql("restore_a") == "-- snapshot A", "missing project folder still backs up and restores database");
        check(Directory.EnumerateFiles(paths.Resolve("data", "recovery"), "database.before.sql", SearchOption.AllDirectories).Any(path => File.ReadAllText(path) == "-- absent project live A"), "missing project folder retains actual database recovery dump");

        Seed("restore_a", "FAIL_IMPORT");
        var broken = await snapshots.CreateProjectSnapshotAsync(project, "failing import");
        Seed("restore_a", "-- current A after broken snapshot"); File.WriteAllText(file, "current files after broken snapshot");
        await Fails(() => snapshots.RestoreProjectSnapshotAsync(broken.DirectoryPath, false), RecoveryOutcome.RolledBack, "SQL import failure rolls back combined restore");
        check(Sql("restore_a") == "-- current A after broken snapshot" && File.ReadAllText(file) == "current files after broken snapshot", "import failure preserves files and recovers SQL");

        using (var cancel = new CancellationTokenSource())
        {
            OperationSafety.Checkpoint = step => { if (step == "snapshot-database-import") cancel.Cancel(); };
            try { await Fails(() => snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, false, cancellationToken: cancel.Token), RecoveryOutcome.RolledBack, "combined restore cancellation"); }
            finally { OperationSafety.Checkpoint = null; }
        }
        check(Sql("restore_a") == "-- current A after broken snapshot" && File.ReadAllText(file) == "current files after broken snapshot", "cancellation compensates with an independent token");

        foreach (var point in new[] { "snapshot-files-installed", "snapshot-vhost-installed" })
        {
            await projects.SetMetadataAsync(project, originalMetadata with { Name = "Live metadata" });
            await store.WriteAsync(hostPath, originalHost with { Port = 8081 });
            OperationSafety.Checkpoint = step => { if (step == point) throw new IOException("Injected combined restore failure"); };
            try { await Fails(() => snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, false), RecoveryOutcome.RolledBack, point); }
            finally { OperationSafety.Checkpoint = null; }
            check(Sql("restore_a") == "-- current A after broken snapshot" && File.ReadAllText(file) == "current files after broken snapshot"
                && (await projects.GetMetadataAsync(project))?.Name == "Live metadata" && (await store.ReadAsync<VirtualHostDefinition>(hostPath))?.Port == 8081,
                point + " restores all previous resources");
        }

        // Failed compensation retains every artifact and can be resumed from its durable journal.
        OperationSafety.Checkpoint = step =>
        {
            if (step != "snapshot-files-installed") return;
            File.WriteAllText(paths.Resolve("fake-state", "fail-all-imports"), "fail");
            throw new IOException("Original combined failure");
        };
        RecoveryOperationException manual;
        try { manual = await Fails(() => snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, false), RecoveryOutcome.ManualRecoveryRequired, "failed combined compensation"); }
        finally { OperationSafety.Checkpoint = null; File.Delete(paths.Resolve("fake-state", "fail-all-imports")); }
        check(manual.Message.Contains("Original combined failure") && manual.Message.Contains("data"), "compensation reports original failure and retained backup paths");
        var journal = Directory.EnumerateFiles(paths.Resolve("www"), "combined-restore.restore-operation-*.json").Single(path =>
            JsonSerializer.Deserialize<ProjectRestoreState>(File.ReadAllText(path))?.Phase is not ("completed" or "rolled-back"));
        await snapshots.RecoverProjectRestoreAsync(journal, project);
        check(Sql("restore_a") == "-- current A after broken snapshot" && File.ReadAllText(file) == "current files after broken snapshot", "durable combined recovery resumes failed compensation");

        // A reassigned or shared resource must cause a pre-mutation abort.
        await store.WriteAsync(hostPath, originalHost with { DocumentRoot = "www/foreign" });
        var foreignBytes = File.ReadAllBytes(hostPath);
        await Fails(() => snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, true), RecoveryOutcome.Unchanged, "reassigned VHost conflict");
        check(File.ReadAllBytes(hostPath).SequenceEqual(foreignBytes), "foreign VHost remains byte-identical");
        await store.WriteAsync(hostPath, originalHost);
        Directory.CreateDirectory(paths.Resolve("www", "database-reference"));
        await projects.SetMetadataAsync("www/database-reference", new ProjectMetadata { Database = "restore_a" });
        await Fails(() => snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, false), RecoveryOutcome.Unchanged, "shared snapshot target database");
        await projects.SetMetadataAsync("www/database-reference", new());

        var directory = paths.Resolve(snapshot.DirectoryPath.Split('/'));
        var manifestPath = Path.Combine(directory, "snapshot.json");
        var manifest = (await store.ReadAsync<ProjectSnapshotManifest>(manifestPath))!;
        await store.WriteAsync(manifestPath, manifest with { Snapshot = manifest.Snapshot with { DatabaseName = "restore_b" }, Metadata = originalMetadata with { Database = "restore_b" } });
        await Fails(() => snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, false), RecoveryOutcome.Unchanged, "tampered historical database receipt");
        await store.WriteAsync(manifestPath, manifest);

        // ZIP integrity and unexpected entries are rejected before the live tree is changed.
        var archivePath = Path.Combine(directory, "files.zip");
        File.Move(archivePath, archivePath + ".saved");
        await Fails(() => snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, false), RecoveryOutcome.Unchanged, "missing ZIP payload");
        File.Move(archivePath + ".saved", archivePath);
        File.Copy(archivePath, archivePath + ".saved", false);
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update))
        {
            zip.GetEntry("index.txt")!.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("index.txt").Open()); writer.Write("tampered files");
        }
        await store.WriteAsync(manifestPath, manifest with { ArchiveSize = new FileInfo(archivePath).Length, ArchiveSha256 = OperationSafety.Hash(archivePath) });
        await Fails(() => snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, false), RecoveryOutcome.Unchanged, "ZIP per-file hash mismatch before resource mutation");
        Exception? corruptFile = null;
        try { ArchiveFileTree.Prepare(directory, root, manifest, await ArchiveFileTree.ReadEntries(directory, manifest, default), new AppLogService(paths), default); }
        catch (InvalidDataException exception) { corruptFile = exception; }
        check(corruptFile?.Message.Contains("index.txt") == true, "ZIP extraction verifies original per-file hash during streaming");
        check(Sql("restore_a") == "-- current A after broken snapshot", "corrupt ZIP file leaves target database untouched");
        File.Delete(archivePath); File.Move(archivePath + ".saved", archivePath);
        using (var zip = ZipFile.Open(archivePath, ZipArchiveMode.Update)) zip.CreateEntry("../control.txt");
        await store.WriteAsync(manifestPath, manifest with { ArchiveSize = new FileInfo(archivePath).Length, ArchiveSha256 = OperationSafety.Hash(archivePath) });
        await Fails(() => snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, false), RecoveryOutcome.Unchanged, "unexpected traversal ZIP entry");
        check(File.ReadAllText(file) == "current files after broken snapshot", "ZIP validation failures preserve live tree");

        var largeRoot = paths.Resolve("www", "large-zip"); Directory.CreateDirectory(largeRoot);
        for (var i = 0; i < 30000; i++) File.WriteAllText(Path.Combine(largeRoot, $"file-{i:D5}.txt"), new string('x', 128));
        var watch = Stopwatch.StartNew();
        long peakManaged = GC.GetTotalMemory(true);
        var progressCount = 0;
        var large = await snapshots.CreateProjectSnapshotAsync("www/large-zip", "30000 entries", progress: new InlineProgress(value => { if (value.StartsWith("files:")) { progressCount++; peakManaged = Math.Max(peakManaged, GC.GetTotalMemory(false)); } }));
        var largeDirectory = paths.Resolve(large.DirectoryPath.Split('/'));
        check(new FileInfo(Path.Combine(largeDirectory, "snapshot.json")).Length < 4096 && progressCount >= 300, "30000-file ZIP snapshot has compact listing header and progress");
        check((await snapshots.GetProjectSnapshotsAsync("www/large-zip")).Count == 1, "large snapshot listing reads small header");
        await snapshots.RestoreProjectSnapshotAsync(large.DirectoryPath, false);
        check(Directory.EnumerateFiles(largeRoot).Count() == 30000, "30000-file ZIP snapshot restores all entries");
        Console.WriteLine($"MEASURE: synthetic 30000 files create+restore {watch.Elapsed.TotalSeconds:F1}s; sampled managed heap {peakManaged / 1024 / 1024} MiB; index {new FileInfo(Path.Combine(largeDirectory, "files.index.json")).Length / 1024} KiB.");
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
}
