using Drageon.Core;
using Drageon.Infrastructure;

internal static class RestoreExchangeRegression
{
    internal static async Task RunAsync(PortablePathService paths, JsonFileStore store, ProjectDiscoveryService projects,
        DatabaseBackupService databases, ProjectSnapshotService snapshots, StagingService staging, IAppLogService logs, Action<bool, string> check)
    {
        var renameSource = paths.Resolve("native-rename-source");
        var renameTarget = paths.Resolve("native-rename-target");
        Directory.CreateDirectory(renameSource);
        File.WriteAllText(Path.Combine(renameSource, "control.txt"), "original");
        SafeFileTree.CreateJunction(Path.Combine(renameSource, "dangling-link"), paths.Resolve("missing-rename-target"));
        OperationSafety.Checkpoint = step => { if (step == "directory-rename") throw new IOException("Injected managed access denial", unchecked((int)0x80070005)); };
        try { OperationSafety.MoveDirectory(renameSource, renameTarget); }
        finally { OperationSafety.Checkpoint = null; }
        check(File.ReadAllText(Path.Combine(renameTarget, "control.txt")) == "original" && new DirectoryInfo(Path.Combine(renameTarget, "dangling-link")).LinkTarget is not null, "native exclusive fallback renames tree with dangling junction after managed access denial");
        OperationSafety.MoveDirectory(renameTarget, renameSource);
        Directory.CreateDirectory(renameTarget); File.WriteAllText(Path.Combine(renameTarget, "foreign.txt"), "foreign");
        try { OperationSafety.MoveDirectory(renameSource, renameTarget); } catch (IOException) { }
        check(File.ReadAllText(Path.Combine(renameTarget, "foreign.txt")) == "foreign" && File.ReadAllText(Path.Combine(renameSource, "control.txt")) == "original", "rename never replaces an existing foreign destination");
        SafeFileTree.Delete(renameTarget);
        using (var cancelled = new CancellationTokenSource())
        {
            OperationSafety.Checkpoint = step => { if (step == "directory-rename") { cancelled.Cancel(); throw new IOException("Injected refusal", unchecked((int)0x80070005)); } };
            try { OperationSafety.MoveDirectory(renameSource, renameTarget, cancelled.Token); } catch (OperationCanceledException) { }
            finally { OperationSafety.Checkpoint = null; }
            check(Directory.Exists(renameSource) && !Directory.Exists(renameTarget), "cancellation prevents native retry before any rename");
        }
        var savedSource = renameSource + ".saved";
        OperationSafety.Checkpoint = step =>
        {
            if (step != "directory-rename") return;
            Directory.Move(renameSource, savedSource); Directory.CreateDirectory(renameSource);
            File.WriteAllText(Path.Combine(renameSource, "foreign.txt"), "replacement");
            throw new IOException("Injected source replacement", unchecked((int)0x80070005));
        };
        try { OperationSafety.MoveDirectory(renameSource, renameTarget); } catch (IOException) { }
        finally { OperationSafety.Checkpoint = null; }
        check(File.ReadAllText(Path.Combine(renameSource, "foreign.txt")) == "replacement" && File.ReadAllText(Path.Combine(savedSource, "control.txt")) == "original" && !Directory.Exists(renameTarget), "native retry preserves replaced source and original saved tree");
        const string source = "www/runtime-source", project = "www/runtime-staging";
        Directory.CreateDirectory(paths.Resolve(source.Split('/')));
        File.WriteAllText(paths.Resolve("www", "runtime-source", "index.txt"), "snapshot");
        Directory.CreateDirectory(paths.Resolve("fake-state", "runtime_original"));
        File.WriteAllText(paths.Resolve("fake-state", "runtime_original.sql"), "-- snapshot database");
        await projects.SetMetadataAsync(source, new ProjectMetadata { Database = "runtime_original" });
        await staging.CreateAsync(new(source, "Runtime staging", project, "http://runtime-staging.localhost", "runtime_staging", true, true));
        var snapshot = await snapshots.CreateProjectSnapshotAsync(project, "runtime lock");
        var file = paths.Resolve("www", "runtime-staging", "index.txt");
        bool Clean() => !Directory.EnumerateFileSystemEntries(paths.Resolve("www")).Any(path => Path.GetFileName(path).StartsWith("runtime-staging.", StringComparison.Ordinal));
        File.WriteAllText(file, "live");
        await snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, true, expectedProjectPath: project);
        check(File.ReadAllText(file) == "snapshot", "staging restore installs snapshot without stopping a server");
        check((await snapshots.GetProjectSnapshotsAsync(project)).Count == 2, "staging restore creates optional current-state snapshot");
        File.WriteAllText(file, "locked live");
        var databaseDirectory = paths.Resolve("fake-state", "runtime_staging");
        var databaseIdentity = OperationSafety.DirectoryIdentity(databaseDirectory);
        using (var foreignLock = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            RecoveryOperationException? failure = null;
            try { await snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, false, expectedProjectPath: project); }
            catch (RecoveryOperationException exception) { failure = exception; }
            check(failure?.Outcome == RecoveryOutcome.RolledBack && failure.Message.Contains("Projektordner konnte nicht umbenannt"), "foreign Windows file lock clearly reports exchange failure and rollback");
            check(File.ReadAllText(file) == "locked live", "failed exchange preserves original files");
            check(OperationSafety.DirectoryIdentity(databaseDirectory) == databaseIdentity, "blocked staging exchange never replaces database incarnation");
            check(Clean(), "failed staging exchange cleans owned preparation and www journals after successful rollback");
        }
        {
            OperationSafety.Checkpoint = step => { if (step == "snapshot-files-installed") throw new IOException("Injected post-install failure"); };
            try { await snapshots.RestoreProjectSnapshotAsync(snapshot.DirectoryPath, false, expectedProjectPath: project); }
            catch (RecoveryOperationException exception) { check(exception.Outcome == RecoveryOutcome.RolledBack, "post-install staging failure rolls back"); }
            finally { OperationSafety.Checkpoint = null; }
            check(Clean() && File.ReadAllText(file) == "locked live", "post-install rollback cleans preparation");
        }
        // Older rollback code could retain a www file journal pointing at an archived fallback.
        var oldJournal = Directory.EnumerateDirectories(paths.Resolve("data", "recovery"), "restore-*")
            .Select(directory => Path.Combine(directory, "restore.operation.json")).Where(File.Exists)
            .First(path => System.Text.Json.JsonSerializer.Deserialize<ProjectRestoreState>(File.ReadAllText(path)) is { Project: project, Phase: "rolled-back" });
        var oldState = System.Text.Json.JsonSerializer.Deserialize<ProjectRestoreState>(File.ReadAllText(oldJournal))!;
        var oldExchange = System.Text.Json.JsonSerializer.Deserialize<FileExchange>(File.ReadAllText(Path.Combine(oldState.Recovery, "files.operation.json")))!;
        OperationSafety.CreateDirectoryExclusive(oldState.Prepared);
        File.WriteAllText(Path.Combine(oldState.Prepared, "old-preparation.txt"), "unused preparation");
        var legacyExchange = oldExchange with { Previous = Path.Combine(oldState.Recovery, "files.before"), PreparedIdentity = OperationSafety.DirectoryIdentity(oldState.Prepared) };
        OperationSafety.Persist(oldState.Prepared.Replace(".prepare-", ".operation-", StringComparison.Ordinal) + ".json", legacyExchange);
        await staging.DeleteAsync(project, true);
        check(Clean() && !Directory.Exists(paths.Resolve("www", "runtime-staging")), "staging deletion removes archived successful and rolled-back restore records");
    }

}
