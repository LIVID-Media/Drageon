using Drageon.Core;
using Drageon.Infrastructure;
using System.Security.AccessControl;
using System.Security.Principal;

internal static class WindowsSafetyRegression
{
    public static async Task RunAsync(PortablePathService paths, JsonFileStore store, ProjectDiscoveryService projects,
        DatabaseBackupService databases, ProjectSnapshotService snapshots, StagingService staging, IAppLogService logs, Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows runtime safety tests require Windows.");
        async Task<Exception> Fails(Func<Task> operation, string name)
        {
            Exception? failure = null;
            try { await operation(); } catch (Exception exception) { failure = exception; }
            check(failure is not null, name);
            return failure!;
        }

        var project = paths.Resolve("www", "safe-files");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "keep.txt"), "snapshot");
        var info = await snapshots.CreateProjectSnapshotAsync("www/safe-files", "regression");
        var directory = paths.Resolve(info.DirectoryPath.Split('/'));
        var manifestPath = Path.Combine(directory, "snapshot.json");
        var zipManifest = (await store.ReadAsync<ProjectSnapshotManifest>(manifestPath))!;
        var zipEntries = await ArchiveFileTree.ReadEntries(directory, zipManifest, default);
        check(zipManifest.FormatVersion == 3 && zipManifest.Entries.Count == 0 && new FileInfo(manifestPath).Length < 4096, "ZIP snapshot has small listing header and separate compact index");
        ArchiveFileTree.Validate(directory, zipManifest, zipEntries);
        var original = zipManifest with { FormatVersion = 2, Entries = SafeFileTree.Capture(project, Path.Combine(directory, "files"), default) };
        await store.WriteAsync(manifestPath, original);
        check(System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(directory), @"^\d{4}-\d{2}-\d{2}_\d{6}_\d{3}$"), "snapshot names use seconds and milliseconds without random suffix");
        await Fails(() => Task.Run(() => OperationSafety.CreateDirectoryExclusive(directory)), "snapshot directory allocation never reuses an existing directory");
        check(File.Exists(manifestPath), "exclusive directory collision preserves existing snapshot");
        check(original.FormatVersion == 2 && original.Entries.Single().Size == 8 && original.Entries.Single().Sha256?.Length == 64, "new snapshot records format, size and checksum");
        File.WriteAllText(Path.Combine(project, "keep.txt"), "live");

        // Metadata edits and competing services must wait for the same mutation gate.
        Task? queuedMetadata = null;
        using (var cancellation = new CancellationTokenSource())
        {
            OperationSafety.Checkpoint = step =>
            {
                if (step != "prepared") return;
                queuedMetadata = projects.SetNameAsync("www/safe-files", "queued", cancellation.Token);
                check(!queuedMetadata.IsCompleted, "metadata update waits during file exchange");
                cancellation.Cancel();
            };
            try { await snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false); }
            finally { OperationSafety.Checkpoint = null; }
            await Fails(() => queuedMetadata!, "queued metadata cancellation cannot mutate operation state");
        }
        File.WriteAllText(Path.Combine(project, "keep.txt"), "live");
        var control = paths.Resolve("control.txt"); File.WriteAllText(control, "control");
        async Task BadManifest(ProjectSnapshotManifest value, string name)
        {
            await store.WriteAsync(manifestPath, value);
            await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false, expectedProjectPath: "www/safe-files"), name);
            check(File.ReadAllText(Path.Combine(project, "keep.txt")) == "live" && File.ReadAllText(control) == "control", name + " leaves live tree and control unchanged");
            await store.WriteAsync(manifestPath, original);
        }
        foreach (var entry in new[] {
            new SnapshotFileEntry("../control.txt", "file"), new SnapshotFileEntry(control, "file"),
            new SnapshotFileEntry("bad:stream", "file"), new SnapshotFileEntry("CON", "file"),
            new SnapshotFileEntry("trailing.", "directory"), new SnapshotFileEntry("a", "unknown"),
            new SnapshotFileEntry("link", "junction"), new SnapshotFileEntry("link", "directory-link", "$PROJECT$/../escape") })
            await BadManifest(original with { Entries = [entry] }, "reject manifest " + entry.Path + "/" + entry.Kind);
        await BadManifest(original with { Entries = [original.Entries.Single(), original.Entries.Single() with { Path = "KEEP.TXT" }] }, "reject case-insensitive duplicate");
        await BadManifest(original with { Entries = [new("a", "junction", paths.AppRoot), new("a/b", "directory")] }, "reject link ancestor conflict");
        await BadManifest(original with { Snapshot = original.Snapshot with { ProjectPath = "www/foreign" } }, "reject foreign manifest project");
        await BadManifest(original with { FormatVersion = 99 }, "reject unknown format version");
        var captured = Path.Combine(directory, "files", "keep.txt");
        File.Move(captured, captured + ".saved");
        await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false), "missing snapshot file detected before mutation");
        check(File.ReadAllText(Path.Combine(project, "keep.txt")) == "live", "missing file preserves project without optional snapshot");
        File.Move(captured + ".saved", captured);
        File.WriteAllText(captured, "tampered");
        await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false), "checksum detects same-size tampering");
        File.WriteAllText(captured, "snapshot");
        await BadManifest(original with { Snapshot = original.Snapshot with { DatabaseName = "missing-dump" } }, "expected SQL dump must exist");
        await BadManifest(original with { Metadata = new ProjectMetadata { StagingIdentity = "foreign" } }, "foreign staging metadata fails before mutation");
        await BadManifest(original with { VirtualHost = new("foreign.localhost", "www/foreign") }, "foreign vhost restore fails before mutation");

        // Manifest DirectoryPath is data, never an address for a delete/list operation.
        await store.WriteAsync(manifestPath, original with { Snapshot = original.Snapshot with { DirectoryPath = paths.AppRoot } });
        check((await snapshots.GetProjectSnapshotsAsync("www/safe-files")).Single().DirectoryPath == info.DirectoryPath, "snapshot address derived from actual location");
        await Fails(() => snapshots.DeleteProjectSnapshotAsync(info.DirectoryPath, expectedProjectPath: "www/foreign"), "delete bound to expected project");
        check(File.Exists(manifestPath) && File.ReadAllText(control) == "control", "foreign snapshot reference cannot delete control files");
        await store.WriteAsync(manifestPath, original);
        await store.WriteAsync(manifestPath, original with { FormatVersion = 0, Entries = original.Entries.Select(entry => entry with { Size = null, Sha256 = null }).ToArray() });
        await snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false);
        check(File.ReadAllText(Path.Combine(project, "keep.txt")) == "snapshot", "legacy nonempty snapshot without checksums remains supported");
        File.WriteAllText(Path.Combine(project, "keep.txt"), "live");
        await store.WriteAsync(manifestPath, original);
        await Fails(() => snapshots.DeleteProjectSnapshotAsync(paths.AppRoot), "reject snapshot root as delete target");
        await Fails(() => Task.Run(() => paths.Resolve("..", Path.GetFileName(paths.AppRoot) + "-sibling", "control")), "portable path checks directory boundary");

        var filesRoot = Path.Combine(directory, "files");
        Directory.Move(filesRoot, filesRoot + "-saved");
        SafeFileTree.CreateJunction(filesRoot, paths.AppRoot);
        await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false), "snapshot parent junction cannot read external files");
        Directory.Delete(filesRoot); Directory.Move(filesRoot + "-saved", filesRoot);
        var snapshotLink = paths.Resolve("data", "snapshots", "safe-files", "linked-snapshot");
        SafeFileTree.CreateJunction(snapshotLink, directory);
        await Fails(() => snapshots.DeleteProjectSnapshotAsync(snapshotLink), "snapshot directory junction cannot redirect deletion");
        Directory.Delete(snapshotLink);
        var ancestor = paths.Resolve("www", "junction-project");
        SafeFileTree.CreateJunction(ancestor, project);
        await Fails(() => snapshots.CreateProjectSnapshotAsync("www/junction-project", "reject"), "project junction rejected before snapshot capture");
        Directory.Delete(ancestor);

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false, cancellationToken: cancelled.Token), "pre-cancelled restore unchanged");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            OperationSafety.Checkpoint = step => { if (step == "prepared") cancelled.Cancel(); };
            try { await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false, cancellationToken: cancelled.Token), "cancellation after preparation leaves project unchanged"); }
            finally { OperationSafety.Checkpoint = null; }
            check(File.ReadAllText(Path.Combine(project, "keep.txt")) == "live", "prepared cancellation preserves live bytes");
        }
        using (var locked = new FileStream(Path.Combine(project, "keep.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false), "Windows lock prevents exchange safely");
        check(File.ReadAllText(Path.Combine(project, "keep.txt")) == "live", "Windows file lock preserves live project");
        OperationSafety.Checkpoint = step => { if (step == "copy") throw new IOException("Injected disk-full copy failure"); };
        try { await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false), "injected disk-full preparation failure preserves project"); }
        finally { OperationSafety.Checkpoint = null; }
        check(File.ReadAllText(Path.Combine(project, "keep.txt")) == "live", "copy failure does not destroy existing project");

        OperationSafety.Checkpoint = step => { if (step == "previous-moved") throw new IOException("Injected exchange failure"); };
        try
        {
            var failure = await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false), "exchange failure invokes rollback");
            check(failure is RecoveryOperationException { Outcome: RecoveryOutcome.RolledBack } && File.ReadAllText(Path.Combine(project, "keep.txt")) == "live", "rollback restores old project");
        }
        finally { OperationSafety.Checkpoint = null; }
        using (var cancellation = new CancellationTokenSource())
        {
            OperationSafety.Checkpoint = step => { if (step == "previous-moved") cancellation.Cancel(); };
            try { await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false, cancellationToken: cancellation.Token), "cancellation after first rename triggers independent rollback"); }
            finally { OperationSafety.Checkpoint = null; }
            check(File.ReadAllText(Path.Combine(project, "keep.txt")) == "live", "cancelled user token does not prevent rollback");
        }
        OperationSafety.Checkpoint = step => { if (step is "previous-moved" or "rollback") throw new IOException("Injected rollback failure"); };
        string recoveryJournal;
        try
        {
            var failure = await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false), "rollback failure is explicit");
            recoveryJournal = Directory.EnumerateFiles(paths.Resolve("www"), "safe-files.operation-*.json").Single(path => File.ReadAllText(path).Contains("previous-moved"));
            var state = System.Text.Json.JsonSerializer.Deserialize<FileExchange>(File.ReadAllText(recoveryJournal))!;
            check(failure.Message.Contains(state.Previous) && File.ReadAllText(Path.Combine(state.Previous, "keep.txt")) == "live", "failed rollback retains and reports old tree path");
        }
        finally { OperationSafety.Checkpoint = null; }
        await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false), "pending journal prevents competing restore");
        await snapshots.RecoverProjectRestoreAsync(recoveryJournal.Replace(".operation-", ".restore-operation-"), "www/safe-files");
        check(File.ReadAllText(Path.Combine(project, "keep.txt")) == "live", "persisted exchange can recover without original user token");

        // Simulate process death with only the durable state and both sibling trees remaining.
        await projects.SetMetadataAsync("www/safe-files", new ProjectMetadata { Favorite = true });
        var id = Guid.NewGuid().ToString("N");
        var old = project + ".previous-" + id; var prepared = project + ".prepare-" + id;
        Directory.Move(project, old); Directory.CreateDirectory(prepared);
        var crashJournal = project + ".operation-" + id + ".json";
        OperationSafety.Persist(crashJournal, new FileExchange(project, prepared, old, true, "prepared", OperationSafety.DirectoryIdentity(old), OperationSafety.DirectoryIdentity(prepared)));
        _ = await projects.GetProjectsAsync();
        check(File.ReadAllText(paths.Resolve("app", "config", "projects.json")).Contains("www/safe-files"), "scan retains metadata during missing-folder exchange window");
        await snapshots.RecoverFileExchangeAsync(crashJournal, "www/safe-files");
        check(File.ReadAllText(Path.Combine(project, "keep.txt")) == "live", "recovery handles death between rename and journal update");
        await projects.SetMetadataAsync("www/safe-files", new ProjectMetadata());

        var plan = await snapshots.GetSnapshotPlanAsync(info.DirectoryPath, "www/safe-files", false);
        File.WriteAllText(Path.Combine(project, "keep.txt"), "new live");
        await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false, expectedProjectPath: "www/safe-files", confirmedPlan: plan.Fingerprint), "changed confirmed snapshot plan requires reconfirmation");
        check(File.ReadAllText(Path.Combine(project, "keep.txt")) == "new live", "stale plan performs no mutation");
        File.WriteAllText(Path.Combine(project, "keep.txt"), "live");

        // Native ACL denial: preparation cannot create a sibling, original bytes must survive.
        var parentInfo = new DirectoryInfo(paths.Resolve("www"));
        var acl = parentInfo.GetAccessControl();
        var denied = parentInfo.GetAccessControl();
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateDirectories, AccessControlType.Deny));
        try
        {
            parentInfo.SetAccessControl(denied);
            await Fails(() => snapshots.RestoreProjectSnapshotAsync(info.DirectoryPath, false), "Windows ACL preparation failure preserves project");
        }
        finally
        {
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorBinaryForm(acl.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
            parentInfo.SetAccessControl(restored);
        }
        check(File.ReadAllText(Path.Combine(project, "keep.txt")) == "live", "ACL failure leaves bytes unchanged");

        var external = paths.Resolve("external-control"); Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "external.txt"), "external");
        SafeFileTree.CreateJunction(Path.Combine(project, "external-link"), external);
        SafeFileTree.CreateJunction(Path.Combine(project, "internal-link"), project);
        var linkSnapshot = await snapshots.CreateProjectSnapshotAsync("www/safe-files", "links");
        OperationSafety.Checkpoint = step => { if (step == "link") throw new IOException("Injected link creation failure"); };
        try { await Fails(() => snapshots.RestoreProjectSnapshotAsync(linkSnapshot.DirectoryPath, false), "link creation failure occurs before old tree mutation"); }
        finally { OperationSafety.Checkpoint = null; }
        check(File.ReadAllText(Path.Combine(project, "keep.txt")) == "live", "link failure preserves original project");
        await snapshots.RestoreProjectSnapshotAsync(linkSnapshot.DirectoryPath, false);
        check(File.ReadAllText(Path.Combine(external, "external.txt")) == "external", "external junction target untouched by restore");
        check(new DirectoryInfo(Path.Combine(project, "internal-link")).ResolveLinkTarget(false)?.FullName.Equals(project, StringComparison.OrdinalIgnoreCase) == true, "internal root junction points to final project after exchange");
        var linkManifest = (await store.ReadAsync<ProjectSnapshotManifest>(Path.Combine(paths.Resolve(linkSnapshot.DirectoryPath.Split('/')), "snapshot.json")))!;
        var linkEntries = await ArchiveFileTree.ReadEntries(paths.Resolve(linkSnapshot.DirectoryPath.Split('/')), linkManifest, default);
        check(!linkEntries.Any(entry => entry.Path.Contains("external.txt")), "capture does not copy external link content");
        var archiveDirectory = paths.Resolve(linkSnapshot.DirectoryPath.Split('/'));
        using (var zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(archiveDirectory, "files.zip")))
        {
            using var reader = new StreamReader(zip.GetEntry("external-link")!.Open());
            check(reader.ReadToEnd() == external && !zip.Entries.Any(entry => entry.FullName.Contains("external.txt")), "ZIP retains external link target text without copying target files");
        }
        var legacyArchive = paths.Resolve("legacy-index-only"); Directory.CreateDirectory(legacyArchive);
        File.Copy(Path.Combine(archiveDirectory, "files.zip"), Path.Combine(legacyArchive, "files.zip"));
        using (var zip = System.IO.Compression.ZipFile.Open(Path.Combine(legacyArchive, "files.zip"), System.IO.Compression.ZipArchiveMode.Update))
            foreach (var entry in zip.Entries.Where(entry => linkEntries.Any(link => link.Path == entry.FullName && link.Kind is "junction" or "file-link" or "directory-link")).ToArray()) entry.Delete();
        ArchiveFileTree.Validate(legacyArchive, linkManifest with { ArchiveSize = new FileInfo(Path.Combine(legacyArchive, "files.zip")).Length, ArchiveSha256 = OperationSafety.Hash(Path.Combine(legacyArchive, "files.zip")) }, linkEntries);
        check(true, "earlier format-3 archives with index-only links remain valid");
        await snapshots.DeleteProjectSnapshotAsync(linkSnapshot.DirectoryPath);
        check(File.ReadAllText(Path.Combine(external, "external.txt")) == "external", "snapshot cleanup does not delete external target");

        var symlink = Path.Combine(project, "symbolic-link");
        var symlinkAvailable = false;
        try { File.CreateSymbolicLink(symlink, Path.Combine(project, "keep.txt")); symlinkAvailable = true; }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { Console.WriteLine("SKIP: Windows symbolic-link privilege unavailable: " + exception.Message); }
        if (symlinkAvailable)
        {
            var symlinkSnapshot = await snapshots.CreateProjectSnapshotAsync("www/safe-files", "symlink");
            await snapshots.RestoreProjectSnapshotAsync(symlinkSnapshot.DirectoryPath, false);
            check(new FileInfo(symlink).ResolveLinkTarget(false)?.FullName.Equals(Path.Combine(project, "keep.txt"), StringComparison.OrdinalIgnoreCase) == true, "Windows file symlink restored to final project");
        }

        var empty = paths.Resolve("www", "empty-project"); Directory.CreateDirectory(empty);
        var emptySnapshot = await snapshots.CreateProjectSnapshotAsync("www/empty-project", "empty");
        File.WriteAllText(Path.Combine(empty, "later.txt"), "later");
        await snapshots.RestoreProjectSnapshotAsync(emptySnapshot.DirectoryPath, false);
        check(!Directory.EnumerateFileSystemEntries(empty).Any(), "legitimate empty snapshot restores empty project");
        var emptyManifestPath = Path.Combine(paths.Resolve(emptySnapshot.DirectoryPath.Split('/')), "snapshot.json");
        var emptyManifest = (await store.ReadAsync<ProjectSnapshotManifest>(emptyManifestPath))!;
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(emptyManifestPath)!, "files"));
        await store.WriteAsync(emptyManifestPath, emptyManifest with { FormatVersion = 0 });
        await snapshots.RestoreProjectSnapshotAsync(emptySnapshot.DirectoryPath, false);
        check(Directory.Exists(empty), "legacy empty snapshot remains supported");

        var collision = paths.Resolve("app", "config", "vhosts", "collision.localhost.json");
        File.WriteAllText(collision, "{\"host\":\"collision.localhost\",\"documentRoot\":\"www/foreign\"}");
        var collisionBytes = File.ReadAllBytes(collision);
        await Fails(() => staging.CreateAsync(new("www/safe-files", "collision", "www/collision-stage", "http://collision.localhost", null, false, true)), "existing vhost collision aborts staging");
        check(File.ReadAllBytes(collision).SequenceEqual(collisionBytes), "existing vhost stays byte-identical after failed staging creation");
        var changedHost = paths.Resolve("app", "config", "vhosts", "changed-during-create.localhost.json");
        OperationSafety.Checkpoint = step =>
        {
            if (step != "staging-vhost-created") return;
            File.WriteAllText(changedHost, "{\"host\":\"changed-during-create.localhost\",\"documentRoot\":\"www/foreign\"}");
            throw new IOException("Injected later creation failure");
        };
        try { await Fails(() => staging.CreateAsync(new("www/safe-files", "changed", "www/changed-stage", "http://changed-during-create.localhost", null, false, true)), "changed vhost during failed creation cannot be cleaned as owned"); }
        finally { OperationSafety.Checkpoint = null; }
        check(File.ReadAllText(changedHost).Contains("www/foreign"), "repurposed creation vhost retained by cleanup");
        FileStream? cleanupLock = null;
        var cleanupHost = paths.Resolve("app", "config", "vhosts", "cleanup-failure.localhost.json");
        OperationSafety.Checkpoint = step =>
        {
            if (step != "staging-vhost-created") return;
            cleanupLock = new FileStream(cleanupHost, FileMode.Open, FileAccess.Read, FileShare.Read);
            throw new IOException("primary-creation-failure");
        };
        try
        {
            var failure = await Fails(() => staging.CreateAsync(new("www/safe-files", "cleanup", "www/cleanup-failure-stage", "http://cleanup-failure.localhost", null, false, true)), "staging cleanup failure remains explicit");
            check(failure.Message.Contains("primary-creation-failure") && failure.Message.Contains(cleanupHost), "cleanup failure does not hide primary error and reports retained vhost");
        }
        finally { OperationSafety.Checkpoint = null; cleanupLock?.Dispose(); }
        var raceTarget = paths.Resolve("www", "race-stage");
        OperationSafety.Checkpoint = step => { if (step == "prepared") { Directory.CreateDirectory(raceTarget); File.WriteAllText(Path.Combine(raceTarget, "foreign.txt"), "foreign"); } };
        try { await Fails(() => staging.CreateAsync(new("www/safe-files", "race", "www/race-stage", "http://race.localhost", null, false, false)), "late foreign staging target never overwritten"); }
        finally { OperationSafety.Checkpoint = null; }
        check(File.ReadAllText(Path.Combine(raceTarget, "foreign.txt")) == "foreign", "late target preserved by staging cleanup");
        var legacyRoot = paths.Resolve("www", "legacy-stage"); Directory.CreateDirectory(legacyRoot); File.WriteAllText(Path.Combine(legacyRoot, "keep.txt"), "legacy");
        await projects.SetMetadataAsync("www/legacy-stage", new ProjectMetadata { StagingOf = "www/safe-files" });
        await Fails(() => staging.DeleteAsync("www/legacy-stage", false), "legacy staging folder ownership is not inferred");
        check(File.ReadAllText(Path.Combine(legacyRoot, "keep.txt")) == "legacy", "legacy staging folder preserved");
        var replaced = await staging.CreateAsync(new("www/safe-files", "replaced", "www/replaced-stage", "http://replaced.localhost", null, false, false));
        var replacedRoot = paths.Resolve(replaced.ProjectPath.Split('/'));
        OperationSafety.Checkpoint = step =>
        {
            if (step != "staging-delete-prepared") return;
            Directory.Move(replacedRoot, replacedRoot + ".saved");
            Directory.CreateDirectory(replacedRoot);
            File.WriteAllText(Path.Combine(replacedRoot, "foreign.txt"), "foreign replacement");
        };
        try { await Fails(() => staging.DeleteAsync(replaced.ProjectPath, false), "late replacement directory cannot be deleted as owned"); }
        finally { OperationSafety.Checkpoint = null; }
        check(File.ReadAllText(Path.Combine(replacedRoot, "foreign.txt")) == "foreign replacement", "file identity protects late foreign directory");

        var stage = await staging.CreateAsync(new("www/safe-files", "owned", "www/owned-stage", "http://owned.localhost", null, false, true));
        check(!Directory.EnumerateFiles(paths.Resolve("www"), "owned-stage.*operation-*.json").Any(), "successful staging creation removes both creation journals");
        var stageRoot = paths.Resolve(stage.ProjectPath.Split('/'));
        var stageSnapshot = await snapshots.CreateProjectSnapshotAsync(stage.ProjectPath, "stage");
        using (var locked = new FileStream(Path.Combine(stageRoot, "keep.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
            await Fails(() => staging.DeleteAsync(stage.ProjectPath, false), "Windows lock staging deletion fails safely");
        check(File.Exists(Path.Combine(paths.Resolve(stageSnapshot.DirectoryPath.Split('/')), "snapshot.json")), "locked project deletion retains snapshots");
        var ownedHost = paths.Resolve("app", "config", "vhosts", "owned.localhost.json");
        var hostBytes = File.ReadAllBytes(ownedHost);
        await store.WriteAsync(ownedHost, new VirtualHostDefinition("owned.localhost", "www/foreign"));
        await Fails(() => staging.DeleteAsync(stage.ProjectPath, false), "repurposed vhost preserved on staging deletion");
        check(File.Exists(ownedHost) && Directory.Exists(stageRoot), "repurposed vhost conflict leaves project intact");
        File.WriteAllBytes(ownedHost, hostBytes);
        await projects.SetMetadataAsync("www/safe-files", new ProjectMetadata { Vhost = "owned.localhost" });
        await Fails(() => staging.DeleteAsync(stage.ProjectPath, false), "shared vhost reference blocks deletion");
        await projects.SetMetadataAsync("www/safe-files", new ProjectMetadata());
        var sharedRootHost = paths.Resolve("app", "config", "vhosts", "shared-root.localhost.json");
        await store.WriteAsync(sharedRootHost, new VirtualHostDefinition("shared-root.localhost", stage.ProjectPath));
        await Fails(() => staging.DeleteAsync(stage.ProjectPath, false), "another vhost sharing staging document root blocks deletion");
        check(Directory.Exists(stageRoot) && File.Exists(sharedRootHost), "shared staging root and foreign vhost preserved");
        File.Delete(sharedRootHost);
        var stageMetadata = (await projects.GetMetadataAsync(stage.ProjectPath))!;
        await projects.SetMetadataAsync(stage.ProjectPath, stageMetadata with { Database = "contao5" });
        var databaseBytes = File.ReadAllBytes(paths.Resolve("fake-state", "contao5.sql"));
        await Fails(() => staging.DeleteAsync(stage.ProjectPath, true), "reassigned main database cannot be deleted");
        await Fails(() => staging.DeleteAsync(stage.ProjectPath, true, new DatabaseConnection(Port: 3307)), "same-named database on another endpoint cannot be deleted");
        check(File.ReadAllBytes(paths.Resolve("fake-state", "contao5.sql")).SequenceEqual(databaseBytes), "reassigned and other-server database bytes preserved");
        await projects.SetMetadataAsync(stage.ProjectPath, stageMetadata with { StagingIdentity = null });
        await Fails(() => staging.DeleteAsync(stage.ProjectPath, false), "legacy staging vhost ownership is not inferred");
        await projects.SetMetadataAsync(stage.ProjectPath, stageMetadata);
        var deletePlan = await staging.GetDeletePlanAsync(stage.ProjectPath, false);
        File.WriteAllText(Path.Combine(stageRoot, "new.txt"), "new");
        await Fails(() => staging.DeleteAsync(stage.ProjectPath, false, confirmedPlan: deletePlan.Fingerprint), "changed staging deletion plan requires reconfirmation");
        await staging.DeleteAsync(stage.ProjectPath, false);
        check(!Directory.EnumerateFileSystemEntries(paths.Resolve("www"), "owned-stage*").Any(), "successful staging deletion leaves no project/recovery/journal artifacts in www");
        check((await snapshots.GetProjectSnapshotsAsync(stage.ProjectPath)).Count == 0, "successful staging deletion removes its snapshots permanently");

        var cleanupStage = await staging.CreateAsync(new("www/safe-files", "cleanup", "www/cleanup-stage", "http://cleanup.localhost", null, false, false));
        OperationSafety.Checkpoint = step => { if (step == "staging-cleanup") throw new IOException("injected cleanup failure"); };
        Exception cleanupError;
        try { cleanupError = await Fails(() => staging.DeleteAsync(cleanupStage.ProjectPath, false), "cleanup failure is reported separately after committed deletion"); }
        finally { OperationSafety.Checkpoint = null; }
        check(cleanupError is RecoveryOperationException { Outcome: RecoveryOutcome.Completed } && !Directory.Exists(paths.Resolve(cleanupStage.ProjectPath.Split('/')))
            && Directory.EnumerateDirectories(paths.Resolve("www"), "cleanup-stage.recovery-*").Any(), "cleanup failure reports completed deletion and retains artifact paths");
        var swappedCleanup = await staging.CreateAsync(new("www/safe-files", "swap-cleanup", "www/swap-cleanup", "http://swap-cleanup.localhost", null, false, false));
        string? foreignRecovery = null;
        OperationSafety.Checkpoint = step =>
        {
            if (step != "staging-cleanup") return;
            foreignRecovery = Directory.EnumerateDirectories(paths.Resolve("www"), "swap-cleanup.recovery-*").Single();
            Directory.Move(foreignRecovery, foreignRecovery + ".saved");
            Directory.CreateDirectory(foreignRecovery);
            File.WriteAllText(Path.Combine(foreignRecovery, "foreign.txt"), "preserve");
        };
        try { await Fails(() => staging.DeleteAsync(swappedCleanup.ProjectPath, false), "cleanup refuses a substituted recovery directory"); }
        finally { OperationSafety.Checkpoint = null; }
        check(File.ReadAllText(Path.Combine(foreignRecovery!, "foreign.txt")) == "preserve", "cleanup preserves foreign replacement files");
        check(Directory.Exists(foreignRecovery + ".saved"), "original recovery tree remains available after replacement conflict");
        check(!Directory.Exists(stageRoot) && !File.Exists(ownedHost), "proven owned vhost removed after project cleanup");
        foreach (var checkpoint in new[] { "staging-project-moved", "staging-project-removed", "staging-resources-removed", "staging-snapshot-moved" })
        {
            var name = "recover-" + checkpoint;
            var brokenStage = await staging.CreateAsync(new("www/safe-files", name, "www/" + name, "http://" + name + ".localhost", null, false, true));
            var brokenSnapshot = await snapshots.CreateProjectSnapshotAsync(brokenStage.ProjectPath, "before delete");
            OperationSafety.Checkpoint = step => { if (step == checkpoint) throw new IOException("Injected " + checkpoint); };
            try { await Fails(() => staging.DeleteAsync(brokenStage.ProjectPath, false), "staging failure journal at " + checkpoint); }
            finally { OperationSafety.Checkpoint = null; }
            var brokenRoot = paths.Resolve(brokenStage.ProjectPath.Split('/'));
            var journal = Directory.EnumerateFiles(paths.Resolve("www"), name + ".delete-operation-*.json").Single();
            var state = System.Text.Json.JsonSerializer.Deserialize<StagingDeletion>(File.ReadAllText(journal))!;
            check(Directory.Exists(paths.Resolve(brokenSnapshot.DirectoryPath.Split('/'))) || Directory.Exists(Path.Combine(state.Recovery, "snapshots", Path.GetFileName(brokenSnapshot.DirectoryPath))), "staging snapshots retained at " + checkpoint);
            await staging.RecoverDeletionAsync(journal, brokenStage.ProjectPath);
            check(File.ReadAllText(Path.Combine(brokenRoot, "keep.txt")) == File.ReadAllText(Path.Combine(project, "keep.txt")) && File.Exists(paths.Resolve("app", "config", "vhosts", name + ".localhost.json")), "staging rollback restores files/vhost/metadata at " + checkpoint);
            check(Directory.Exists(paths.Resolve(brokenSnapshot.DirectoryPath.Split('/'))), "staging recovery restores snapshot address at " + checkpoint);
        }

        await projects.SetDatabaseAsync("www/safe-files", "contao5");
        var databaseStage = await staging.CreateAsync(new("www/safe-files", "database-owned", "www/database-owned", "http://database-owned.localhost", "owned_stage_db", true, true));
        var databaseMetadata = (await projects.GetMetadataAsync(databaseStage.ProjectPath))!;
        var databaseProofPath = paths.Resolve("app", "config", "staging", databaseMetadata.StagingIdentity! + ".json");
        var databaseProof = (await store.ReadAsync<StagingOwnership>(databaseProofPath))!;
        check(databaseProof.DatabaseIdentity is not null, "staging records local server and created schema directory identities");
        await Fails(() => staging.DeleteAsync(databaseStage.ProjectPath, true, new DatabaseConnection(Port: 3307)), "staging DB deletion rejects another server endpoint");
        await projects.SetDatabaseAsync("www/safe-files", "owned_stage_db");
        await Fails(() => staging.DeleteAsync(databaseStage.ProjectPath, true), "shared staging database cannot be deleted");
        await projects.SetDatabaseAsync("www/safe-files", "contao5");
        await projects.SetDatabaseAsync(databaseStage.ProjectPath, "contao5");
        await Fails(() => staging.DeleteAsync(databaseStage.ProjectPath, true), "reassigned main database stays protected with valid old DB proof");
        await projects.SetDatabaseAsync(databaseStage.ProjectPath, "owned_stage_db");
        await store.WriteAsync(databaseProofPath, databaseProof with { DatabaseIdentity = null });
        await Fails(() => staging.DeleteAsync(databaseStage.ProjectPath, true), "legacy database without incarnation proof is preserved");
        await store.WriteAsync(databaseProofPath, databaseProof);
        var activeServersPath = paths.Resolve("app", "config", "packages.active.json");
        var activeServers = (await store.ReadAsync<Dictionary<string, string>>(activeServersPath))!;
        await store.WriteAsync(activeServersPath, new Dictionary<string, string> { ["mariadb"] = "test" });
        await Fails(() => staging.DeleteAsync(databaseStage.ProjectPath, true), "same-named DB on different server component is preserved");
        var mariaStage = await staging.CreateAsync(new("www/safe-files", "maria-owned", "www/maria-owned", "http://maria-owned.localhost", "maria_owned_db", true, false));
        var mariaMetadata = (await projects.GetMetadataAsync(mariaStage.ProjectPath))!;
        check((await store.ReadAsync<StagingOwnership>(paths.Resolve("app", "config", "staging", mariaMetadata.StagingIdentity! + ".json")))?.ServerIdentity?.StartsWith("mariadb|") == true, "MariaDB staging captures physical server ownership");
        await staging.DeleteAsync(mariaStage.ProjectPath, true);
        check(!File.Exists(paths.Resolve("fake-state", "maria_owned_db.sql")), "MariaDB owned staging DB is deleted");
        await store.WriteAsync(activeServersPath, activeServers);
        var serverSystemDirectory = paths.Resolve("fake-state", "mysql");
        Directory.Move(serverSystemDirectory, serverSystemDirectory + ".saved"); Directory.CreateDirectory(serverSystemDirectory);
        await Fails(() => staging.DeleteAsync(databaseStage.ProjectPath, true), "reinitialized server storage cannot be mistaken for original instance");
        Directory.Delete(serverSystemDirectory); Directory.Move(serverSystemDirectory + ".saved", serverSystemDirectory);
        File.WriteAllText(paths.Resolve("fake-state", "fail-dump"), "");
        try { await Fails(() => staging.DeleteAsync(databaseStage.ProjectPath, true), "failed staging SQL backup aborts before project mutation"); }
        finally { File.Delete(paths.Resolve("fake-state", "fail-dump")); }
        check(Directory.Exists(paths.Resolve(databaseStage.ProjectPath.Split('/'))) && File.Exists(paths.Resolve("fake-state", "owned_stage_db.sql")), "SQL backup failure preserves both project and database");
        var schemaDirectory = paths.Resolve("fake-state", "owned_stage_db");
        Directory.Move(schemaDirectory, schemaDirectory + ".old"); Directory.CreateDirectory(schemaDirectory);
        await Fails(() => staging.DeleteAsync(databaseStage.ProjectPath, true), "recreated same-name database directory cannot be deleted");
        check(File.ReadAllText(paths.Resolve("fake-state", "owned_stage_db.sql")).Contains("project database") && Directory.Exists(paths.Resolve(databaseStage.ProjectPath.Split('/'))), "DB ownership conflicts leave database and project untouched");
        Directory.Delete(schemaDirectory); Directory.Move(schemaDirectory + ".old", schemaDirectory);
        var databasePlan = await staging.GetDeletePlanAsync(databaseStage.ProjectPath, true);
        check(databasePlan.BackupScope == "stagingDatabase" && databasePlan.TargetDatabase == "owned_stage_db", "staging plan explicitly includes its database deletion");
        await staging.DeleteAsync(databaseStage.ProjectPath, true, confirmedPlan: databasePlan.Fingerprint);
        check(!File.Exists(paths.Resolve("fake-state", "owned_stage_db.sql")) && !Directory.EnumerateFileSystemEntries(paths.Resolve("www"), "database-owned*").Any(), "proven staging DB deletion leaves no SQL or project artifacts");

        foreach (var step in new[] { "staging-database-drop", "staging-database-removed" })
        {
            var stageName = step.Replace("staging-", "");
            var databaseName = stageName.Replace('-', '_');
            var recoverDbStage = await staging.CreateAsync(new("www/safe-files", stageName, "www/" + stageName, "http://" + stageName + ".localhost", databaseName, true, true));
            OperationSafety.Checkpoint = checkpoint => { if (checkpoint == step) throw new IOException("injected DB deletion failure"); };
            try { await Fails(() => staging.DeleteAsync(recoverDbStage.ProjectPath, true), "staging database recovery journal at " + step); }
            finally { OperationSafety.Checkpoint = null; }
            var dbJournal = Directory.EnumerateFiles(paths.Resolve("www"), stageName + ".delete-operation-*.json").Single();
            var stageDatabaseState = System.Text.Json.JsonSerializer.Deserialize<StagingDeletion>(File.ReadAllText(dbJournal))!;
            check(File.ReadAllText(Path.Combine(stageDatabaseState.Recovery, "database.sql")).Contains("project database"), "staging SQL recovery dump retained at " + step);
            await staging.RecoverDeletionAsync(dbJournal, recoverDbStage.ProjectPath);
            check(File.ReadAllText(paths.Resolve("fake-state", databaseName + ".sql")).Contains("project database") && Directory.Exists(paths.Resolve(recoverDbStage.ProjectPath.Split('/'))), "staging recovers files and database at " + step);
            await staging.DeleteAsync(recoverDbStage.ProjectPath, true);
            check(!File.Exists(paths.Resolve("fake-state", databaseName + ".sql")) && !Directory.EnumerateFileSystemEntries(paths.Resolve("www"), stageName + "*").Any(), "recovered DB proof supports complete retry deletion at " + step);
        }
        var foreignDbStage = await staging.CreateAsync(new("www/safe-files", "foreign-recovery", "www/foreign-recovery", "http://foreign-recovery.localhost", "foreign_recovery_db", true, false));
        OperationSafety.Checkpoint = step => { if (step == "staging-database-removed") throw new IOException("injected interruption after DROP"); };
        try { await Fails(() => staging.DeleteAsync(foreignDbStage.ProjectPath, true), "DB deletion interruption retains recovery before foreign recreation"); }
        finally { OperationSafety.Checkpoint = null; }
        var foreignDbJournal = Directory.EnumerateFiles(paths.Resolve("www"), "foreign-recovery.delete-operation-*.json").Single();
        Directory.CreateDirectory(paths.Resolve("fake-state", "foreign_recovery_db")); File.WriteAllText(paths.Resolve("fake-state", "foreign_recovery_db.sql"), "foreign database");
        await Fails(() => staging.RecoverDeletionAsync(foreignDbJournal, foreignDbStage.ProjectPath), "staging DB recovery refuses a late foreign same-name database");
        check(File.ReadAllText(paths.Resolve("fake-state", "foreign_recovery_db.sql")) == "foreign database" && !Directory.Exists(paths.Resolve(foreignDbStage.ProjectPath.Split('/'))), "foreign DB recovery conflict causes no resource overwrite");
        var failedImportStage = await staging.CreateAsync(new("www/safe-files", "failed-recovery", "www/failed-recovery", "http://failed-recovery.localhost", "failed_recovery_db", true, false));
        OperationSafety.Checkpoint = step => { if (step == "staging-database-removed") throw new IOException("injected interruption"); };
        try { await Fails(() => staging.DeleteAsync(failedImportStage.ProjectPath, true), "staging DB recovery import test reaches saved deletion state"); }
        finally { OperationSafety.Checkpoint = null; }
        var failedImportJournal = Directory.EnumerateFiles(paths.Resolve("www"), "failed-recovery.delete-operation-*.json").Single();
        File.WriteAllText(paths.Resolve("fake-state", "fail-all-imports"), "");
        try { await Fails(() => staging.RecoverDeletionAsync(failedImportJournal, failedImportStage.ProjectPath), "failed staging SQL recovery import cannot report success"); }
        finally { File.Delete(paths.Resolve("fake-state", "fail-all-imports")); }
        await Fails(() => staging.RecoverDeletionAsync(failedImportJournal, failedImportStage.ProjectPath), "unfinished staging SQL recovery requires explicit manual intervention");
        check(!Directory.Exists(paths.Resolve(failedImportStage.ProjectPath.Split('/'))) && File.Exists(failedImportJournal), "partial staging SQL recovery leaves durable state and avoids further file mutation");
        var cancelledDbStage = await staging.CreateAsync(new("www/safe-files", "cancelled-drop", "www/cancelled-drop", "http://cancelled-drop.localhost", "cancelled_drop_db", true, false));
        using (var dropCancellation = new CancellationTokenSource())
        {
            OperationSafety.Checkpoint = step => { if (step == "staging-database-drop") dropCancellation.Cancel(); };
            try { await Fails(() => staging.DeleteAsync(cancelledDbStage.ProjectPath, true, cancellationToken: dropCancellation.Token), "cancellation during staging DROP cannot return success"); }
            finally { OperationSafety.Checkpoint = null; }
        }
        var cancelledDbJournal = Directory.EnumerateFiles(paths.Resolve("www"), "cancelled-drop.delete-operation-*.json").Single();
        check(File.Exists(cancelledDbJournal) && !File.Exists(paths.Resolve("fake-state", "cancelled_drop_db.sql")), "cancelled DROP preserves journal after confirmed database removal");
        await staging.RecoverDeletionAsync(cancelledDbJournal, cancelledDbStage.ProjectPath);
        check(File.Exists(paths.Resolve("fake-state", "cancelled_drop_db.sql")) && Directory.Exists(paths.Resolve(cancelledDbStage.ProjectPath.Split('/'))), "cancelled staging DROP restores files and database without cancelled token");
        await staging.DeleteAsync(cancelledDbStage.ProjectPath, true);
        await projects.SetDatabaseAsync("www/safe-files", null);

        foreach (var stamp in new[] { "2026-10-10_120428", "2026-10-10_120428-7a2619ee", "2026-10-10_120428.560-7a2619ee" })
        {
            var legacySqlPath = paths.Resolve("data", "backups", "databases", "legacy_db_" + stamp + ".sql"); File.WriteAllText(legacySqlPath, "legacy");
            check((await databases.GetBackupsAsync()).Any(item => item.Path == legacySqlPath && item.DatabaseName == "legacy_db"), "legacy backup naming retains database association: " + stamp);
            File.Delete(legacySqlPath);
        }

        var sql = paths.Resolve("data", "backups", "databases", "regression.sql");
        var dbState = paths.Resolve("fake-state", "restore-target.sql");
        File.WriteAllText(dbState, "-- target old, independent of project association");
        File.WriteAllText(sql, "FAIL_IMPORT");
        var dbFailure = await Fails(() => databases.RestoreAsync(sql, "restore-target"), "database import failure returns failure status");
        check(dbFailure.Message.Contains("zurückgerollt") && File.ReadAllText(dbState).Contains("target old"), "database import failure restores actual target independently of project folder");
        check(Directory.EnumerateFiles(paths.Resolve("data", "backups", "databases"), "restore-target_recovery_*.sql").Any(path => File.ReadAllText(path).Contains("target old")), "actual target recovery SQL retained");
        File.WriteAllText(paths.Resolve("fake-state", "fail-all-imports"), "");
        try
        {
            dbFailure = await Fails(() => databases.RestoreAsync(sql, "restore-target"), "failed database compensation reports failure");
            check(dbFailure.Message.Contains("Manuelle") && dbFailure.Message.Contains(".sql"), "manual database recovery reports dump and journal paths");
        }
        finally { File.Delete(paths.Resolve("fake-state", "fail-all-imports")); }
        var pendingDatabaseJournal = Directory.EnumerateFiles(paths.Resolve("data", "backups", "databases"), "restore-target_recovery_*.sql.operation.json")
            .Single(path => File.ReadAllText(path).Contains("before-replace"));
        var pendingDump = pendingDatabaseJournal[..^".operation.json".Length];
        await Fails(() => databases.DeleteBackupAsync(pendingDump), "unfinished database recovery dump cannot be deleted");
        check(File.Exists(pendingDump), "required recovery SQL remains available after attempted delete");
        await databases.RecoverDatabaseAsync(pendingDatabaseJournal, "restore-target");
        check(File.ReadAllText(dbState).Contains("target old"), "durable database journal can recover after failed compensation");
        File.WriteAllText(dbState, "-- target old");
        File.WriteAllText(sql, "SLOW_IMPORT");
        using (var cancellation = new CancellationTokenSource())
        {
            File.Delete(paths.Resolve("fake-state", "import-started"));
            var task = databases.RestoreAsync(sql, "restore-target", cancellationToken: cancellation.Token);
            for (var attempts = 0; attempts < 500 && !File.Exists(paths.Resolve("fake-state", "import-started")); attempts++) await Task.Delay(10);
            check(File.Exists(paths.Resolve("fake-state", "import-started")), "database cancellation test reaches import");
            using var competingCancellation = new CancellationTokenSource();
            var competing = new DatabaseBackupService(paths, store, logs).RestoreAsync(sql, "restore-target", cancellationToken: competingCancellation.Token);
            check(!competing.IsCompleted, "separate database service instances share resource gate");
            competingCancellation.Cancel();
            await Fails(() => competing, "competing database operation cancels without mutation");
            using var deleteCancellation = new CancellationTokenSource();
            var queuedDelete = databases.DeleteBackupAsync(sql, deleteCancellation.Token);
            check(!queuedDelete.IsCompleted, "backup deletion waits for active database restore");
            deleteCancellation.Cancel();
            await Fails(() => queuedDelete, "queued backup deletion cancels without mutation");
            check(File.Exists(sql), "active restore source remains available");
            cancellation.Cancel();
            await Fails(() => task, "database cancellation does not return success");
            check(File.ReadAllText(dbState) == "-- target old", "cancelled import is terminated before token-independent compensation");
        }
        File.WriteAllText(sql, "-- new");
        var dbPlan = await databases.GetRestorePlanAsync(sql, "restore-target");
        File.WriteAllText(sql, "-- changed plan");
        await Fails(() => databases.RestoreAsync(sql, "restore-target", confirmedPlan: dbPlan.Fingerprint), "changed SQL plan rejected before drop");
        check(File.ReadAllText(dbState) == "-- target old", "stale database plan preserves target");
        await Fails(() => databases.CreateDatabaseFromAsync("contao5", "restore-target"), "exclusive CREATE rejects existing database");
        check(File.ReadAllText(dbState) == "-- target old", "copy collision never issues DROP IF EXISTS");
        File.WriteAllText(paths.Resolve("fake-state", "import-fail-source.sql"), "FAIL_IMPORT");
        var copyFailure = await Fails(() => databases.CreateDatabaseFromAsync("import-fail-source", "partial-copy"), "failed exclusive database copy is tracked");
        check(copyFailure.Message.Contains("Manuelle") && File.Exists(paths.Resolve("fake-state", "partial-copy.sql")) && Directory.EnumerateFiles(paths.Resolve("app", "var", "tmp"), "staging-*.sql").Any(), "failed import retains created database and source dump for explicit recovery");
        var externalSqlRoot = paths.Resolve("external-sql"); Directory.CreateDirectory(externalSqlRoot);
        var externalSql = Path.Combine(externalSqlRoot, "control.sql"); File.WriteAllText(externalSql, "external SQL control");
        var sqlJunction = paths.Resolve("data", "backups", "databases", "linked-parent");
        SafeFileTree.CreateJunction(sqlJunction, externalSqlRoot);
        await Fails(() => databases.DeleteBackupAsync(Path.Combine(sqlJunction, "control.sql")), "database backup delete rejects reparse parent");
        check(File.ReadAllText(externalSql) == "external SQL control", "database backup delete preserves external control SQL");
        var server = await databases.GetActiveServerAsync();
        await Fails(() => databases.DumpToPathAsync(server, "contao5", Path.Combine(sqlJunction, "new.sql"), CancellationToken.None), "database dump rejects reparse destination parent");
        check(!File.Exists(Path.Combine(externalSqlRoot, "new.sql")), "database dump cannot write outside backup tree through junction");
        Directory.Delete(sqlJunction);
        File.WriteAllText(paths.Resolve("fake-state", "-named.sql"), "-- old dash database");
        File.WriteAllText(sql, "-- restored dash database");
        await databases.RestoreAsync(sql, "-named");
        check(File.ReadAllText(paths.Resolve("fake-state", "-named.sql")) == "-- restored dash database", "database names beginning with dash cannot become client options");
    }
}
