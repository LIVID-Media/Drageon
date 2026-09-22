using Drageon.Core;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Drageon.Infrastructure;

public sealed class ProjectSnapshotService(
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
        foreach (var manifestPath in Directory.EnumerateFiles(root, "snapshot.json", SearchOption.AllDirectories))
        {
            try
            {
                var manifest = await store.ReadAsync<ProjectSnapshotManifest>(manifestPath, cancellationToken).ConfigureAwait(false);
                if (manifest is not null && manifest.Snapshot.ProjectPath.Equals(key, StringComparison.OrdinalIgnoreCase)) results.Add(manifest.Snapshot);
            }
            catch (JsonException) { }
        }
        SnapshotDirectoryCleanup.PruneEmptyTree(root);
        return results.OrderByDescending(item => item.CreatedAt).ToArray();
    }

    public async Task<ProjectSnapshotInfo> CreateProjectSnapshotAsync(string projectPath, string description, DatabaseConnection? connection = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var relative = NormalizeProjectPath(projectPath);
        var source = paths.Resolve(relative.Split('/'));
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Projektordner nicht gefunden: {relative}");
        var metadata = await projects.GetMetadataAsync(relative, cancellationToken).ConfigureAwait(false);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff", System.Globalization.CultureInfo.InvariantCulture);
        var final = paths.Resolve("data", "snapshots", Path.GetFileName(relative), stamp);
        Directory.CreateDirectory(final);
        try
        {
            progress?.Report("files");
            var entries = SafeFileTree.Capture(source, Path.Combine(final, "files"), cancellationToken);
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
            var manifest = new ProjectSnapshotManifest { Snapshot = info, Entries = entries, Metadata = metadata, VirtualHost = vhost };
            await store.WriteAsync(Path.Combine(final, "snapshot.json"), manifest, cancellationToken).ConfigureAwait(false);
            logs.Write("Snapshot", $"Projekt-Snapshot erstellt: {relative} ({stamp})");
            return info;
        }
        catch (Exception exception)
        {
            SafeFileTree.Delete(final);
            SnapshotDirectoryCleanup.RemoveEmptyAncestors(Path.GetDirectoryName(final)!, paths.Resolve("data", "snapshots"));
            logs.Write("Snapshot", $"Snapshot für {relative} unvollständig: {exception.Message}", LogSeverity.Error);
            throw;
        }
    }

    public async Task RestoreProjectSnapshotAsync(string snapshotDirectory, bool backupCurrentState = true, DatabaseConnection? connection = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var directory = ValidateSnapshotDirectory(snapshotDirectory);
        var manifest = await store.ReadAsync<ProjectSnapshotManifest>(Path.Combine(directory, "snapshot.json"), cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Snapshot-Metadaten fehlen.");
        var target = paths.Resolve(manifest.Snapshot.ProjectPath.Split('/'));
        var currentMetadata = Directory.Exists(target) ? await projects.GetMetadataAsync(manifest.Snapshot.ProjectPath, cancellationToken).ConfigureAwait(false) : null;
        if (backupCurrentState && Directory.Exists(target))
            await CreateProjectSnapshotAsync(manifest.Snapshot.ProjectPath, "Automatisch vor Wiederherstellung", connection, progress, cancellationToken).ConfigureAwait(false);
        progress?.Report("files");
        SafeFileTree.Restore(Path.Combine(directory, "files"), target, manifest.Entries, logs, cancellationToken);
        if (manifest.Snapshot.DatabaseName is not null && File.Exists(Path.Combine(directory, "database.sql")))
        {
            progress?.Report("database");
            await databases.RestoreAsync(Path.Combine(directory, "database.sql"), manifest.Snapshot.DatabaseName, connection, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        progress?.Report("vhost");
        if (manifest.VirtualHost is not null)
        {
            if (!string.IsNullOrWhiteSpace(currentMetadata?.Vhost) && !currentMetadata.Vhost.Equals(manifest.VirtualHost.Host, StringComparison.OrdinalIgnoreCase))
            {
                var obsolete = Path.Combine(paths.Resolve("app", "config", "vhosts"), VHostFileName(currentMetadata.Vhost));
                if (File.Exists(obsolete)) File.Delete(obsolete);
            }
            await store.WriteAsync(Path.Combine(paths.Resolve("app", "config", "vhosts"), VHostFileName(manifest.VirtualHost.Host)), manifest.VirtualHost, cancellationToken).ConfigureAwait(false);
        }
        progress?.Report("metadata");
        await projects.SetMetadataAsync(manifest.Snapshot.ProjectPath, manifest.Metadata ?? new ProjectMetadata(), cancellationToken).ConfigureAwait(false);
        logs.Write("Snapshot", $"Projekt-Snapshot wiederhergestellt: {manifest.Snapshot.ProjectPath}");
    }

    public Task DeleteProjectSnapshotAsync(string snapshotDirectory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = ValidateSnapshotDirectory(snapshotDirectory);
        SafeFileTree.Delete(directory);
        SnapshotDirectoryCleanup.RemoveEmptyAncestors(Path.GetDirectoryName(directory)!, paths.Resolve("data", "snapshots"));
        return Task.CompletedTask;
    }

    private async Task<VirtualHostDefinition?> FindVirtualHostAsync(string? hostName, string projectDirectory, CancellationToken cancellationToken)
    {
        foreach (var file in Directory.EnumerateFiles(paths.Resolve("app", "config", "vhosts"), "*.json"))
        {
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
                entries.Add(new(childRelative.Replace('\\', '/'), "file"));
                var target = Path.Combine(destinationRoot, childRelative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(item.FullName, target, true);
                File.SetLastWriteTimeUtc(target, item.LastWriteTimeUtc);
            }
        }
    }

    public static void Restore(string capturedFiles, string targetRoot, IReadOnlyList<SnapshotFileEntry> entries, IAppLogService logs, CancellationToken cancellationToken)
    {
        Delete(targetRoot); Directory.CreateDirectory(targetRoot);
        foreach (var entry in entries.Where(item => item.Kind == "directory").OrderBy(item => item.Path.Count(ch => ch == '/')))
            Directory.CreateDirectory(ToPath(targetRoot, entry.Path));
        foreach (var entry in entries.Where(item => item.Kind == "file"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ToPath(targetRoot, entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(ToPath(capturedFiles, entry.Path), target, true);
        }
        foreach (var entry in entries.Where(item => item.Kind.EndsWith("-link", StringComparison.Ordinal) || item.Kind == "junction"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ToPath(targetRoot, entry.Path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                var linkTarget = RestoreLinkTarget(targetRoot, entry.LinkTarget!);
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
    }

    private static string NormalizeLinkTarget(string sourceRoot, FileSystemInfo item, string linkTarget)
    {
        if (!Path.IsPathRooted(linkTarget)) return linkTarget;
        var fullTarget = Path.GetFullPath(linkTarget);
        var relative = Path.GetRelativePath(sourceRoot, fullTarget);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? InternalTargetPrefix + relative.Replace('\\', '/')
            : linkTarget;
    }

    private static string RestoreLinkTarget(string targetRoot, string linkTarget) =>
        linkTarget.StartsWith(InternalTargetPrefix, StringComparison.Ordinal)
            ? Path.Combine(targetRoot, linkTarget[InternalTargetPrefix.Length..].Replace('/', Path.DirectorySeparatorChar))
            : linkTarget;

    private static void CreateJunction(string path, string linkTarget)
    {
        var absoluteTarget = Path.GetFullPath(Path.IsPathRooted(linkTarget) ? linkTarget : Path.Combine(Path.GetDirectoryName(path)!, linkTarget));
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("/d"); info.ArgumentList.Add("/c"); info.ArgumentList.Add("mklink"); info.ArgumentList.Add("/J"); info.ArgumentList.Add(path); info.ArgumentList.Add(absoluteTarget);
        using var process = Process.Start(info) ?? throw new IOException("Junction konnte nicht erstellt werden.");
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(process.StandardError.ReadToEnd().Trim());
    }

    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileReadAttributes = 0x80;
    private static uint GetReparseTag(string path)
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
        if (!Directory.Exists(path) && !File.Exists(path)) return;
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null) { info.Delete(); return; }
        foreach (var item in info.EnumerateFileSystemInfos())
        {
            if (item.LinkTarget is not null) item.Delete();
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
