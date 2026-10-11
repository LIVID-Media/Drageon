using Drageon.Core;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Drageon.Infrastructure;

public sealed partial class ProjectSnapshotService(
    IPortablePathService paths,
    JsonFileStore store,
    IProjectDiscoveryService projects,
    DatabaseBackupService databases,
    IAppLogService logs) : IProjectSnapshotService
{
    public async Task<IReadOnlyList<ProjectSnapshotInfo>> GetProjectSnapshotsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var key = NormalizeProjectPath(projectPath);
        var root = paths.Resolve("data", "snapshots", Path.GetFileName(key));
        if (!Directory.Exists(root)) return [];
        var results = new List<ProjectSnapshotInfo>();
        OperationSafety.NoLinks(root);
        foreach (var manifestPath in Directory.EnumerateDirectories(root).Select(directory => Path.Combine(directory, "snapshot.json")))
        {
            try
            {
                OperationSafety.NoLinks(manifestPath);
                var manifest = await store.ReadAsync<ProjectSnapshotManifest>(manifestPath, cancellationToken).ConfigureAwait(false);
                if (manifest is not null && manifest.Snapshot.ProjectPath.Equals(key, StringComparison.OrdinalIgnoreCase)) { ValidateSnapshotDirectory(Path.GetDirectoryName(manifestPath)!); results.Add(manifest.Snapshot with { DirectoryPath = ToPortable(Path.GetDirectoryName(manifestPath)!) }); }
            }
            catch (JsonException) { }
        }

        return results.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    private async Task<ProjectSnapshotInfo> CreateProjectSnapshotCoreAsync(string projectPath, string description, DatabaseConnection? connection = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var relative = NormalizeProjectPath(projectPath);
        var source = paths.Resolve(relative.Split('/'));
        OperationSafety.NoLinks(source);
        OperationSafety.AssertNoPending(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Projektordner nicht gefunden: {relative}");
        var metadata = await projects.GetMetadataAsync(relative, cancellationToken).ConfigureAwait(false);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture);
        var final = paths.Resolve("data", "snapshots", Path.GetFileName(relative), stamp);
        OperationSafety.NoLinks(final);
        OperationSafety.CreateDirectoryExclusive(final);
        try
        {
            progress?.Report("files");
            await Task.Run(() => ArchiveFileTree.Capture(source, final, cancellationToken, progress), cancellationToken).ConfigureAwait(false);
            progress?.Report("links");
            DatabaseServerInfo? server = null;
            if (!string.IsNullOrWhiteSpace(metadata?.Database))
            {
                progress?.Report("database");
                server = await databases.GetActiveServerAsync(connection, cancellationToken).ConfigureAwait(false);
                await databases.DumpToPathAsync(server, metadata.Database, Path.Combine(final, "database.sql"), cancellationToken).ConfigureAwait(false);
            }
            progress?.Report("metadata");
            var vhost = await FindVirtualHostAsync(metadata?.Vhost, source, cancellationToken).ConfigureAwait(false);
            var info = new ProjectSnapshotInfo(ToPortable(final), relative, metadata?.Name ?? Path.GetFileName(relative), DateTimeOffset.Now, description.Trim(), metadata?.Database,
                server?.DisplayName, server?.Version, IncludesVirtualHost: vhost is not null);
            var sqlPath = Path.Combine(final, "database.sql");
            var manifest = new ProjectSnapshotManifest
            {
                FormatVersion = 3,
                ArchiveSize = new FileInfo(Path.Combine(final, "files.zip")).Length,
                ArchiveSha256 = OperationSafety.Hash(Path.Combine(final, "files.zip")),
                IndexSha256 = OperationSafety.Hash(Path.Combine(final, "files.index.json")),
                Snapshot = info,
                Entries = [],
                Metadata = metadata,
                VirtualHost = vhost,
                DatabaseSize = server is null ? null : new FileInfo(sqlPath).Length,
                DatabaseSha256 = server is null ? null : OperationSafety.Hash(sqlPath),
                DatabaseServerIdentity = server is null ? null : await databases.GetInstanceIdentityAsync(server, cancellationToken).ConfigureAwait(false)
            };
            await store.WriteAsync(Path.Combine(final, "snapshot.json"), manifest, cancellationToken).ConfigureAwait(false);
            await store.WriteAsync(ReceiptPath(final), new SnapshotReceipt(relative, ToPortable(final), OperationSafety.Hash(Path.Combine(final, "snapshot.json"))), cancellationToken).ConfigureAwait(false);
            logs.Write("Snapshot", $"Projekt-Snapshot erstellt: {relative} ({stamp})");
            return info;
        }
        catch (Exception exception)
        {
            try { SafeFileTree.Delete(final); SnapshotDirectoryCleanup.RemoveEmptyAncestors(Path.GetDirectoryName(final)!, paths.Resolve("data", "snapshots")); }
            catch (Exception cleanup) { throw new RecoveryOperationException(RecoveryOutcome.ManualRecoveryRequired, $"{exception.Message}; Unvollständiger Snapshot erhalten: {final}; Cleanup: {cleanup.Message}", exception); }
            logs.Write("Snapshot", $"Snapshot für {relative} unvollständig: {exception.Message}", LogSeverity.Error);
            throw;
        }
    }

    internal async Task DeleteProjectSnapshotCoreAsync(string snapshotDirectory, CancellationToken cancellationToken = default, string? expectedProjectPath = null, string? confirmedPlan = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = ValidateSnapshotDirectory(snapshotDirectory);
        var manifest = await store.ReadAsync<ProjectSnapshotManifest>(Path.Combine(directory, "snapshot.json"), cancellationToken).ConfigureAwait(false) ?? throw new InvalidDataException("Snapshot-Metadaten fehlen.");
        ValidateProjectBinding(directory, manifest, expectedProjectPath);
        if (confirmedPlan is not null) CheckPlan(confirmedPlan, await GetSnapshotPlanAsync(snapshotDirectory, manifest.Snapshot.ProjectPath, true, cancellationToken).ConfigureAwait(false));
        SafeFileTree.Delete(directory);
        RemoveSnapshotReceipt(directory, manifest.Snapshot.ProjectPath);
        SnapshotDirectoryCleanup.RemoveEmptyAncestors(Path.GetDirectoryName(directory)!, paths.Resolve("data", "snapshots"));

    }

    internal void RemoveSnapshotReceipt(string directory, string project)
    {
        var receipt = ReceiptPath(directory); OperationSafety.NoLinks(receipt);
        if (!File.Exists(receipt)) return;
        var saved = JsonSerializer.Deserialize<SnapshotReceipt>(File.ReadAllText(receipt), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (saved?.Project != project || saved.Directory != ToPortable(directory)) throw new IOException($"Unklarer Snapshot-Nachweis bleibt erhalten: {receipt}");
        File.Delete(receipt);
    }

    internal async Task QuarantineSnapshotCoreAsync(string snapshotDirectory, string expectedProjectPath, string recoveryRoot)
    {
        var directory = ValidateSnapshotDirectory(snapshotDirectory);
        var manifest = await store.ReadAsync<ProjectSnapshotManifest>(Path.Combine(directory, "snapshot.json")).ConfigureAwait(false) ?? throw new InvalidDataException("Snapshot-Metadaten fehlen.");
        ValidateProjectBinding(directory, manifest, expectedProjectPath);
        var destination = Path.Combine(recoveryRoot, "snapshots", Path.GetFileName(directory));
        OperationSafety.NoLinks(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        OperationSafety.MoveDirectory(directory, destination);
        SnapshotDirectoryCleanup.RemoveEmptyAncestors(Path.GetDirectoryName(directory)!, paths.Resolve("data", "snapshots"));
    }

    private async Task<VirtualHostDefinition?> FindVirtualHostAsync(string? hostName, string projectDirectory, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(paths.Resolve("app", "config", "vhosts"), "*.json"))
        {
            OperationSafety.NoLinks(file);
            var host = await store.ReadAsync<VirtualHostDefinition>(file, cancellationToken).ConfigureAwait(false);
            if (host is null) continue;
            if (!string.IsNullOrWhiteSpace(hostName) && host.Host.Equals(hostName, StringComparison.OrdinalIgnoreCase)) return host;
            var root = Path.GetFullPath(Path.IsPathRooted(host.DocumentRoot) ? host.DocumentRoot : Path.Combine(paths.AppRoot, host.DocumentRoot.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(projectDirectory, root);
            if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) return host;
        }
        return null;
    }

    private string NormalizeProjectPath(string value)
    {
        var metadata = new ProjectMetadataStore(paths, store);
        return metadata.NormalizeProjectPath(value);
    }
    private string ValidateSnapshotDirectory(string value)
    {
        var full = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(paths.AppRoot, value.Replace('/', Path.DirectorySeparatorChar))); var root = paths.Resolve("data", "snapshots"); var relative = Path.GetRelativePath(root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || !File.Exists(Path.Combine(full, "snapshot.json"))) throw new InvalidDataException("Ungültiger Projekt-Snapshot.");
        OperationSafety.NoLinks(full);
        OperationSafety.NoLinks(Path.Combine(full, "snapshot.json"));
        var location = Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar);
        if (location.Length != 2) throw new InvalidDataException("Snapshot muss direkt im Projekt-Snapshotordner liegen.");
        return full;
    }
    private static string VHostFileName(string host) { var invalid = Path.GetInvalidFileNameChars(); var safe = new string(host.Trim().ToLowerInvariant().Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()); return safe + ".json"; }
    private string ToPortable(string value) => Path.GetRelativePath(paths.AppRoot, value).Replace('\\', '/');
}

internal static class SafeFileTree
{
    private const string InternalTargetPrefix = "$PROJECT$/";
    public static IReadOnlyList<SnapshotFileEntry> Capture(string sourceRoot, string destinationRoot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        OperationSafety.NoLinks(sourceRoot);
        OperationSafety.NoLinks(destinationRoot);
        Directory.CreateDirectory(destinationRoot);
        var entries = new List<SnapshotFileEntry>();
        CaptureDirectory(sourceRoot, sourceRoot, destinationRoot, "", entries, cancellationToken);
        return entries;
    }

    private static void CaptureDirectory(string sourceRoot, string source, string destinationRoot, string relative, List<SnapshotFileEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (relative.Length > 0) entries.Add(new(relative.Replace('\\', '/'), "directory"));
        foreach (var item in new DirectoryInfo(source).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childRelative = Path.Combine(relative, item.Name);
            var linkTarget = item.LinkTarget;
            if (linkTarget is null && (item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Unbekannter Reparse Point: {item.FullName}");
            if (linkTarget is not null)
            {
                var kind = item is DirectoryInfo
                    ? (GetReparseTag(item.FullName) == IoReparseTagMountPoint ? "junction" : "directory-link")
                    : "file-link";
                entries.Add(new(childRelative.Replace('\\', '/'), kind, NormalizeLinkTarget(sourceRoot, item, linkTarget)));
                continue;
            }
            if (item is DirectoryInfo directory)
            {
                CaptureDirectory(sourceRoot, directory.FullName, destinationRoot, childRelative, entries, cancellationToken);
            }
            else
            {

                var target = Path.Combine(destinationRoot, childRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(item.FullName, target, true);
                File.SetLastWriteTimeUtc(target, item.LastWriteTimeUtc);
                entries.Add(new(childRelative.Replace('\\', '/'), "file", Size: new FileInfo(target).Length, Sha256: OperationSafety.Hash(target)));
            }
        }
    }

    public static string Prepare(string capturedFiles, string finalRoot, IReadOnlyList<SnapshotFileEntry> entries, IAppLogService logs, CancellationToken cancellationToken, Action<SnapshotFileEntry, string>? copyFile = null)
    {
        var targetRoot = finalRoot + ".prepare-" + Guid.NewGuid().ToString("N");
        OperationSafety.NoLinks(finalRoot);
        OperationSafety.CreateDirectoryExclusive(targetRoot);
        try
        {
            Directory.CreateDirectory(targetRoot);
            foreach (var entry in entries.Where(item => item.Kind == "directory").OrderBy(item => item.Path.Count(ch => ch == '/')))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.CreateDirectory(ToPath(targetRoot, entry.Path));
            }
            foreach (var entry in entries.Where(item => item.Kind == "file"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                OperationSafety.Checkpoint?.Invoke("copy");
                var target = ToPath(targetRoot, entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (copyFile is null) File.Copy(ToPath(capturedFiles, entry.Path), target, false); else copyFile(entry, target);
                if (copyFile is null && ((entry.Size is not null && new FileInfo(target).Length != entry.Size) || (entry.Sha256 is not null && OperationSafety.Hash(target) != entry.Sha256))) throw new InvalidDataException($"Integritätsfehler beim Kopieren: {entry.Path}");
            }
            foreach (var entry in entries.Where(item => item.Kind.EndsWith("-link", StringComparison.Ordinal) || item.Kind == "junction"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = ToPath(targetRoot, entry.Path);
                OperationSafety.NoLinks(Path.GetDirectoryName(target)!);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try
                {
                    OperationSafety.Checkpoint?.Invoke("link");
                    var linkTarget = RestoreLinkTarget(finalRoot, entry.LinkTarget!);
                    if (entry.Kind == "junction") CreateJunction(target, linkTarget);
                    else if (entry.Kind == "directory-link") Directory.CreateSymbolicLink(target, linkTarget);
                    else File.CreateSymbolicLink(target, linkTarget);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logs.Write("Snapshot", $"Link konnte nicht wiederhergestellt werden: {entry.Path}: {exception.Message}", LogSeverity.Error);
                    throw new IOException($"Link konnte nicht wiederhergestellt werden: {entry.Path}. {exception.Message}", exception);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return targetRoot;
        }
        catch
        {
            try { Delete(targetRoot); } catch (Exception cleanup) { logs.Write("Snapshot", $"Vorbereitung erhalten: {targetRoot}; {cleanup.Message}", LogSeverity.Warning); }
            throw;
        }
    }

    public static void Restore(string capturedFiles, string targetRoot, IReadOnlyList<SnapshotFileEntry> entries, IAppLogService logs, CancellationToken cancellationToken, bool requireAbsent = false)
    {
        Validate(capturedFiles, entries);
        cancellationToken.ThrowIfCancellationRequested();
        OperationSafety.NoLinks(targetRoot);
        if (!requireAbsent) OperationSafety.AssertNoPending(targetRoot);
        var prepared = Prepare(capturedFiles, targetRoot, entries, logs, cancellationToken);
        Exchange(prepared, targetRoot, logs, cancellationToken, requireAbsent);
    }

    public static string Exchange(string targetRoot, string finalRoot, IAppLogService logs, CancellationToken cancellationToken, bool requireAbsent = false, bool keepJournal = false)
    {
        var id = targetRoot[(finalRoot + ".prepare-").Length..];
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Ungültiger Vorbereitungsordner.");
        var previous = finalRoot + ".previous-" + id;
        var journal = finalRoot + ".operation-" + id + ".json";
        try
        {
            OperationSafety.Checkpoint?.Invoke("prepared");
            cancellationToken.ThrowIfCancellationRequested();
            if (requireAbsent && (Directory.Exists(finalRoot) || File.Exists(finalRoot))) throw new IOException("Ziel wurde zwischenzeitlich angelegt.");
            var state = new FileExchange(finalRoot, targetRoot, previous, !requireAbsent && Directory.Exists(finalRoot), "prepared",
                Directory.Exists(finalRoot) ? OperationSafety.DirectoryIdentity(finalRoot) : null, OperationSafety.DirectoryIdentity(targetRoot));
            var oldMoved = false; var installed = false;
            OperationSafety.Persist(journal, state);
            try
            {
                if (state.HadPrevious)
                {
                    try { OperationSafety.MoveDirectory(finalRoot, previous, cancellationToken); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw new IOException($"Projektordner konnte nicht umbenannt werden: {finalRoot}. Offene Dateien, Ordnersperren oder fehlende Berechtigungen verhindern den Austausch. {exception.Message}", exception);
                    }
                    oldMoved = true;
                }
                OperationSafety.Persist(journal, state with { Phase = "previous-moved" });
                OperationSafety.Checkpoint?.Invoke("previous-moved");
                cancellationToken.ThrowIfCancellationRequested();
                OperationSafety.MoveDirectory(targetRoot, finalRoot, cancellationToken);
                installed = true;
                cancellationToken.ThrowIfCancellationRequested();
                OperationSafety.Persist(journal, state with { Phase = "completed" });
                logs.Write("Snapshot", $"Abgeschlossen. Rückfallordner: {previous}; Operationszustand: {journal}");
            }
            catch (Exception original)
            {
                try
                {
                    OperationSafety.Checkpoint?.Invoke("rollback");
                    if (installed)
                    {
                        if (OperationSafety.DirectoryIdentity(finalRoot) != state.PreparedIdentity) throw new IOException("Fremder Ersatzordner bleibt erhalten.");
                        OperationSafety.MoveDirectory(finalRoot, targetRoot);
                    }
                    if (oldMoved) OperationSafety.MoveDirectory(previous, finalRoot);
                    OperationSafety.Persist(journal, state with { Phase = "rolled-back" });
                }
                catch (Exception rollback)
                {
                    throw new RecoveryOperationException(RecoveryOutcome.ManualRecoveryRequired, $"Manuelle Wiederherstellung erforderlich. Alter Stand: {previous}; Zustand: {journal}. {original.Message}; Rollback: {rollback.Message}", original);
                }
                throw new RecoveryOperationException(oldMoved || installed ? RecoveryOutcome.RolledBack : RecoveryOutcome.Unchanged, $"{(oldMoved || installed ? "Erfolgreich zurückgerollt" : "Unverändert abgebrochen")}. {original.Message}", original);
            }
            if (!state.HadPrevious && !keepJournal)
            {
                try { File.Delete(journal); }
                catch (Exception cleanup) { logs.Write("Snapshot", $"Erstellung abgeschlossen; Operationsdatei konnte nicht entfernt werden: {journal}; {cleanup.Message}", LogSeverity.Warning); }
            }
            return journal;
        }
        catch (Exception exception) when (!File.Exists(journal))
        {
            throw new RecoveryOperationException(RecoveryOutcome.Unchanged, $"Unverändert abgebrochen: {exception.Message}", exception);
        }
        finally
        {
            if (!File.Exists(journal) && Directory.Exists(targetRoot))
                try { Delete(targetRoot); } catch (Exception cleanup) { logs.Write("Snapshot", $"Vorbereitung erhalten: {targetRoot}; Cleanup: {cleanup.Message}", LogSeverity.Warning); }
        }
    }

    public static void Validate(string capturedFiles, IReadOnlyList<SnapshotFileEntry> entries, int version = 0)
    {
        OperationSafety.NoLinks(capturedFiles);
        if (!Directory.Exists(capturedFiles)) throw new InvalidDataException("Snapshot-Dateibaum fehlt.");
        if (version is not (0 or 2)) throw new InvalidDataException("Unbekanntes Snapshot-Format.");
        ValidateEntries(entries, version == 2);
        foreach (var entry in entries)
        {
            if (entry.Kind == "file")
            {
                var source = ToPath(capturedFiles, entry.Path);
                OperationSafety.NoLinks(source);
                if (!File.Exists(source)) throw new InvalidDataException($"Snapshot-Datei fehlt: {entry.Path}");
                if (version == 2 && (entry.Size is null || entry.Sha256 is null)) throw new InvalidDataException("Integritätsdaten fehlen.");
                if ((entry.Size is not null && new FileInfo(source).Length != entry.Size) || (entry.Sha256 is not null && !OperationSafety.Hash(source).Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"Integritätsfehler: {entry.Path}");
            }
        }
    }

    internal static void ValidateEntries(IReadOnlyList<SnapshotFileEntry> entries, bool requireIntegrity)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            OperationSafety.Relative(entry.Path);
            if (entry.Kind is not ("file" or "directory" or "junction" or "directory-link" or "file-link") || !seen.TryAdd(entry.Path, entry.Kind))
                throw new InvalidDataException($"Unbekannter oder doppelter Snapshot-Eintrag: {entry.Path}");
            if (entry.Kind is "junction" or "directory-link" or "file-link")
            {
                if (string.IsNullOrWhiteSpace(entry.LinkTarget)) throw new InvalidDataException("Linkziel fehlt.");
                if (entry.LinkTarget.IndexOf('\0') >= 0) throw new InvalidDataException("Ungültiges Linkziel.");
                if (entry.LinkTarget.StartsWith(InternalTargetPrefix, StringComparison.Ordinal) && entry.LinkTarget.Length > InternalTargetPrefix.Length) OperationSafety.Relative(entry.LinkTarget[InternalTargetPrefix.Length..]);
            }
            if (entry.Kind == "file" && requireIntegrity && (entry.Size is null or < 0 || entry.Sha256 is null || entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit))) throw new InvalidDataException("Integritätsdaten fehlen.");

        }
        foreach (var path in seen.Keys)
        {
            var parent = path;
            while (parent.Contains('/'))
            {
                parent = parent[..parent.LastIndexOf('/')];
                if (seen.TryGetValue(parent, out var kind) && kind != "directory") throw new InvalidDataException($"Widersprüchlicher Snapshot-Pfad: {path}");
            }
        }
    }

    internal static string NormalizeLinkTarget(string sourceRoot, FileSystemInfo item, string linkTarget)
    {
        if (!Path.IsPathRooted(linkTarget)) linkTarget = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(item.FullName)!, linkTarget));
        var fullTarget = Path.GetFullPath(linkTarget);
        var relative = Path.GetRelativePath(sourceRoot, fullTarget);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? InternalTargetPrefix + (relative == "." ? "" : relative.Replace('\\', '/'))
            : linkTarget;
    }

    private static string RestoreLinkTarget(string targetRoot, string linkTarget) =>
        linkTarget.StartsWith(InternalTargetPrefix, StringComparison.Ordinal)
            ? Path.Combine(targetRoot, linkTarget[InternalTargetPrefix.Length..].Replace('/', Path.DirectorySeparatorChar))
            : linkTarget;

    internal static void CreateJunction(string path, string linkTarget)
    {
        var absoluteTarget = Path.GetFullPath(Path.IsPathRooted(linkTarget) ? linkTarget : Path.Combine(Path.GetDirectoryName(path)!, linkTarget));
        // Native reparse creation avoids passing untrusted manifest strings to cmd.exe.
        var nativeTarget = absoluteTarget.StartsWith(@"\\", StringComparison.Ordinal) ? @"\??\UNC\" + absoluteTarget[2..] : @"\??\" + absoluteTarget;
        var substitute = System.Text.Encoding.Unicode.GetBytes(nativeTarget + "\0");
        var printable = System.Text.Encoding.Unicode.GetBytes(absoluteTarget + "\0");
        var buffer = new byte[16 + substitute.Length + printable.Length];
        BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)(buffer.Length - 8)).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)(substitute.Length - 2)).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)substitute.Length).CopyTo(buffer, 12);
        BitConverter.GetBytes((ushort)(printable.Length - 2)).CopyTo(buffer, 14);
        substitute.CopyTo(buffer, 16); printable.CopyTo(buffer, 16 + substitute.Length);
        Directory.CreateDirectory(path);
        using var handle = CreateFile(path, 0x40000000, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new IOException($"Junction konnte nicht erstellt werden: {Marshal.GetLastWin32Error()}");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputLength, IntPtr output, int outputLength, out int returned, IntPtr overlapped);
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileReadAttributes = 0x80;
    internal static uint GetReparseTag(string path)
    {
        using var handle = CreateFile(path, FileReadAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 9, out var information, (uint)Marshal.SizeOf<FileAttributeTagInfo>())) return 0;
        return information.ReparseTag;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileAttributeTagInfo { public uint FileAttributes; public uint ReparseTag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes, FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileAttributeTagInfo information, uint bufferSize);

    public static void Delete(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        var info = new DirectoryInfo(path);
        if ((attributes & FileAttributes.Directory) == 0) { File.Delete(path); return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0) { info.Delete(); return; }
        foreach (var item in info.EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) item.Delete();
            else if (item is DirectoryInfo directory) Delete(directory.FullName);
            else { item.Attributes = FileAttributes.Normal; item.Delete(); }
        }
        info.Delete();
    }
    private static string ToPath(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
}

internal static class SnapshotDirectoryCleanup
{
    public static void PruneEmptyTree(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var directory in Directory.EnumerateDirectories(root).ToArray())
        {
            var info = new DirectoryInfo(directory); if (info.LinkTarget is not null) continue;
            PruneEmptyTree(directory);
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
        }
    }

    public static void RemoveEmptyAncestors(string directory, string stopDirectory)
    {
        var stop = Path.GetFullPath(stopDirectory);
        var current = Path.GetFullPath(directory);
        while (!current.Equals(stop, StringComparison.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(stop, current);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || !Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any()) return;
            Directory.Delete(current);
            var parent = Path.GetDirectoryName(current); if (parent is null) return; current = parent;
        }
    }
}
