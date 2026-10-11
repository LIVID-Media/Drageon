using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Drageon.Core;

namespace Drageon.Infrastructure;

// Links carry their target text, never the target contents. The index preserves Windows link kinds.
internal static class ArchiveFileTree
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static IReadOnlyList<SnapshotFileEntry> Capture(string source, string directory, CancellationToken token, IProgress<string>? progress = null, bool compress = true)
    {
        OperationSafety.NoLinks(source); OperationSafety.NoLinks(directory);
        Directory.CreateDirectory(directory);
        var entries = new List<SnapshotFileEntry>();
        var buffer = new byte[128 * 1024];
        using (var stream = new FileStream(Path.Combine(directory, "files.zip"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            void Walk(string current, string relative)
            {
                token.ThrowIfCancellationRequested();
                if (relative.Length > 0) entries.Add(new(relative, "directory"));
                foreach (var item in new DirectoryInfo(current).EnumerateFileSystemInfos())
                {
                    token.ThrowIfCancellationRequested();
                    var name = relative.Length == 0 ? item.Name : relative + "/" + item.Name;
                    OperationSafety.Relative(name);
                    if (item.LinkTarget is string link)
                    {
                        var kind = item is DirectoryInfo ? SafeFileTree.GetReparseTag(item.FullName) == 0xA0000003 ? "junction" : "directory-link" : "file-link";
                        var itemEntry = new SnapshotFileEntry(name, kind, SafeFileTree.NormalizeLinkTarget(source, item, link));
                        entries.Add(itemEntry);
                        var zipLink = zip.CreateEntry(name, CompressionLevel.NoCompression);
                        zipLink.ExternalAttributes = unchecked((int)0xA1FF0000);
                        using var output = zipLink.Open();
                        output.Write(Encoding.UTF8.GetBytes(ArchiveLinkTarget(itemEntry)));
                    }
                    else if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Unbekannter Reparse Point: {item.FullName}");
                    else if (item is DirectoryInfo child) Walk(child.FullName, name);
                    else
                    {
                        OperationSafety.NoLinks(item.FullName);
                        var entry = zip.CreateEntry(name, compress ? CompressionLevel.Fastest : CompressionLevel.NoCompression);
                        if (item.LastWriteTime.Year is >= 1980 and <= 2107) entry.LastWriteTime = item.LastWriteTime;
                        using var input = new FileStream(item.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, FileOptions.SequentialScan);
                        using var output = entry.Open();
                        var (size, hash) = Copy(input, output, buffer, token);
                        entries.Add(new(name, "file", Size: size, Sha256: hash, ModifiedUtcTicks: item.LastWriteTimeUtc.Ticks));
                    }
                    if (entries.Count % 100 == 0) progress?.Report("files:" + entries.Count);
                }
            }
            Walk(source, "");
        }
        using (var index = new FileStream(Path.Combine(directory, "files.index.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
            JsonSerializer.Serialize(index, entries, Options);
        return entries;
    }

    public static async Task<IReadOnlyList<SnapshotFileEntry>> ReadEntries(string directory, ProjectSnapshotManifest manifest, CancellationToken token)
    {
        if (manifest.FormatVersion != 3) return manifest.Entries;
        var index = Path.Combine(directory, "files.index.json");
        OperationSafety.NoLinks(index);
        if (manifest.IndexSha256 is null || OperationSafety.Hash(index) != manifest.IndexSha256) throw new InvalidDataException("Snapshot-Dateiindex: Integritätsfehler.");
        await using var stream = File.OpenRead(index);
        return await JsonSerializer.DeserializeAsync<List<SnapshotFileEntry>>(stream, Options, token).ConfigureAwait(false) ?? throw new InvalidDataException("Snapshot-Dateiindex fehlt.");
    }

    public static void Validate(string directory, ProjectSnapshotManifest manifest, IReadOnlyList<SnapshotFileEntry> entries, bool verifyArchiveContent = true)
    {
        if (manifest.FormatVersion != 3) { SafeFileTree.Validate(Path.Combine(directory, "files"), entries, manifest.FormatVersion); return; }
        var archive = Path.Combine(directory, "files.zip");
        OperationSafety.NoLinks(archive);
        if (manifest.ArchiveSize is null || manifest.ArchiveSha256 is null || new FileInfo(archive).Length != manifest.ArchiveSize || verifyArchiveContent && OperationSafety.Hash(archive) != manifest.ArchiveSha256)
            throw new InvalidDataException("Snapshot-ZIP: Integritätsfehler.");
        SafeFileTree.ValidateEntries(entries, requireIntegrity: true);
        using var zip = ZipFile.OpenRead(archive);
        var expected = entries.Where(e => e.Kind == "file").ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        var links = entries.Where(e => e.Kind is "junction" or "directory-link" or "file-link").ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (expected.Remove(entry.FullName, out var item) && entry.Length == item.Size) continue;
            if (links.Remove(entry.FullName, out var link))
            {
                var target = Encoding.UTF8.GetBytes(ArchiveLinkTarget(link));
                if ((entry.ExternalAttributes & unchecked((int)0xF0000000)) == unchecked((int)0xA0000000) && entry.Length == target.Length)
                {
                    using var input = entry.Open();
                    var actual = new byte[target.Length]; input.ReadExactly(actual);
                    if (actual.AsSpan().SequenceEqual(target)) continue;
                }
            }
            throw new InvalidDataException($"Unerwarteter oder widersprüchlicher ZIP-Eintrag: {entry.FullName}");
        }
        if (expected.Count != 0) throw new InvalidDataException("Snapshot-ZIP: Dateien fehlen.");
        // Earlier format-3 archives stored links only in the index; these remain readable.
    }

    private static string ArchiveLinkTarget(SnapshotFileEntry entry)
    {
        const string prefix = "$PROJECT$/";
        var target = entry.LinkTarget!;
        if (!target.StartsWith(prefix, StringComparison.Ordinal)) return target;
        // Resolve relative to the link parent, independent of the original project location.
        var anchor = Path.GetPathRoot(Environment.CurrentDirectory)!;
        return Path.GetRelativePath(Path.Combine(anchor, Path.GetDirectoryName(entry.Path) ?? ""), Path.Combine(anchor, target[prefix.Length..])).Replace('\\', '/');
    }

    public static string Prepare(string directory, string target, ProjectSnapshotManifest manifest, IReadOnlyList<SnapshotFileEntry> entries, IAppLogService logs, CancellationToken token, IProgress<string>? progress = null)
    {
        if (manifest.FormatVersion != 3) return SafeFileTree.Prepare(Path.Combine(directory, "files"), target, entries, logs, token);
        OperationSafety.NoLinks(Path.Combine(directory, "files.zip"));
        using var zip = ZipFile.OpenRead(Path.Combine(directory, "files.zip"));
        var buffer = new byte[128 * 1024];
        var processed = 0;
        return SafeFileTree.Prepare("", target, entries, logs, token, (entry, path) =>
        {
            var zipEntry = zip.GetEntry(entry.Path) ?? throw new InvalidDataException($"ZIP-Datei fehlt: {entry.Path}");
            using (var input = zipEntry.Open())
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, FileOptions.SequentialScan))
            {
                var (size, hash) = Copy(input, output, buffer, token);
                if (size != entry.Size || hash != entry.Sha256) throw new InvalidDataException($"Integritätsfehler: {entry.Path}");
            }
            File.SetLastWriteTimeUtc(path, entry.ModifiedUtcTicks is long ticks ? new DateTime(ticks, DateTimeKind.Utc) : zipEntry.LastWriteTime.UtcDateTime);
            processed++;
            if (processed % 100 == 0) progress?.Report("files:" + processed);
        });
    }

    private static (long Size, string Hash) Copy(Stream input, Stream output, byte[] buffer, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read); hash.AppendData(buffer, 0, read); size += read;
        }
        return (size, Convert.ToHexString(hash.GetHashAndReset()));
    }

    internal static (long Size, string Hash) CopyFile(string source, string target, CancellationToken token)
    {
        OperationSafety.NoLinks(source); OperationSafety.NoLinks(target);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.SequentialScan);
        return Copy(input, output, new byte[128 * 1024], token);
    }
}
