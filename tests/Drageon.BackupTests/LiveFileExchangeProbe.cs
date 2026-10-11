using System.Text.Json;
using Drageon.Core;
using Drageon.Infrastructure;

// Explicit opt-in live diagnostic. It never imports SQL and always returns the original tree.
internal static class LiveFileExchangeProbe
{
    internal static async Task<int> RunAsync(string[] args)
    {
        const string root = @"C:\Drageon\www\contao5-staging";
        if (args.Length != 2) throw new ArgumentException("Explicit snapshot directory required.");
        var snapshot = Path.GetFullPath(args[1]);
        if (Path.GetDirectoryName(snapshot) != @"C:\Drageon\data\snapshots\contao5-staging") throw new ArgumentException("Unexpected live snapshot path.");
        OperationSafety.NoLinks(root); OperationSafety.NoLinks(snapshot);
        var manifest = JsonSerializer.Deserialize<ProjectSnapshotManifest>(await File.ReadAllTextAsync(Path.Combine(snapshot, "snapshot.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (manifest.Snapshot.ProjectPath != "www/contao5-staging") throw new InvalidDataException("Snapshot binding mismatch.");
        var entries = await ArchiveFileTree.ReadEntries(snapshot, manifest, default);
        ArchiveFileTree.Validate(snapshot, manifest, entries);
        var before = Inventory(root);
        var oldIdentity = OperationSafety.DirectoryIdentity(root);
        var logs = new ProbeLog();
        var prepared = ArchiveFileTree.Prepare(snapshot, root, manifest, entries, logs, default);
        var preparedIdentity = OperationSafety.DirectoryIdentity(prepared);
        var id = prepared[(root + ".prepare-").Length..];
        var previous = root + ".previous-" + id;
        var journal = root + ".operation-" + id + ".json";
        try
        {
            SafeFileTree.Exchange(prepared, root, logs, default, keepJournal: true);
            Console.WriteLine("LIVE SNAPSHOT FILE EXCHANGE SUCCESS: " + entries.Count + " entries");
        }
        finally
        {
            if (Directory.Exists(previous))
            {
                if (OperationSafety.DirectoryIdentity(previous) != oldIdentity || OperationSafety.DirectoryIdentity(root) != preparedIdentity || Directory.Exists(prepared)) throw new IOException("Manual recovery required: " + previous);
                OperationSafety.MoveDirectory(root, prepared);
                OperationSafety.MoveDirectory(previous, root);
            }
            if (OperationSafety.DirectoryIdentity(root) != oldIdentity || !Inventory(root).SequenceEqual(before)) throw new IOException("Original live inventory mismatch; inspect " + previous);
            Console.WriteLine("ORIGINAL LIVE TREE VERIFIED: file hashes, links, directory identity unchanged; SQL untouched");
            if (Directory.Exists(prepared))
            {
                if (OperationSafety.DirectoryIdentity(prepared) != preparedIdentity) throw new IOException("Foreign preparation retained: " + prepared);
                SafeFileTree.Delete(prepared);
            }
            if (File.Exists(journal))
            {
                var state = JsonSerializer.Deserialize<FileExchange>(File.ReadAllText(journal))!;
                if (state.Target != root || state.PreparedIdentity != preparedIdentity || state.PreviousIdentity != oldIdentity) throw new IOException("Foreign journal retained: " + journal);
                File.Delete(journal);
            }
            Console.WriteLine("LIVE PROBE ARTIFACTS REMOVED");
        }
        return 0;
    }

    private static string[] Inventory(string root)
    {
        var result = new List<string>();
        void Walk(string directory)
        {
            foreach (var item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                var relative = Path.GetRelativePath(root, item.FullName);
                if (item.LinkTarget is string link) result.Add(relative + "|link|" + link);
                else if (item is DirectoryInfo child) { result.Add(relative + "|directory"); Walk(child.FullName); }
                else result.Add(relative + "|file|" + OperationSafety.Hash(item.FullName));
            }
        }
        Walk(root); return result.Order(StringComparer.Ordinal).ToArray();
    }

    private sealed class ProbeLog : IAppLogService
    {
        public event EventHandler<LogEntry>? EntryAdded { add { } remove { } }
        public IReadOnlyList<LogEntry> GetRecent(int maximum = 1000) => [];
        public void Write(string component, string message, LogSeverity severity = LogSeverity.Information) => Console.WriteLine(component + ": " + message);
        public void Clear() { }
    }
}
