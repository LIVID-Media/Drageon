using System.Collections.Concurrent;
using System.Text;
using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed class AppLogService(IPortablePathService paths) : IAppLogService
{
    private const int Capacity = 1000;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly object _fileLock = new();
    private readonly string _filePath = paths.Resolve("app", "var", "logs", "app", "runtime.log");

    public event EventHandler<LogEntry>? EntryAdded;

    public IReadOnlyList<LogEntry> GetRecent(int maximum = 1000) =>
        _entries.Reverse().Take(Math.Max(1, maximum)).Reverse().ToArray();

    public void Write(string component, string message, LogSeverity severity = LogSeverity.Information)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var entry = new LogEntry(DateTimeOffset.Now, component, message.TrimEnd(), severity);
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity) _entries.TryDequeue(out _);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            lock (_fileLock)
            {
                File.AppendAllText(_filePath, Format(entry) + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Protokollierung darf die Serverprozesse nicht beeinflussen.
        }

        EntryAdded?.Invoke(this, entry);
    }

    public void Clear()
    {
        _entries.Clear();
        try
        {
            lock (_fileLock)
            {
                File.WriteAllText(_filePath, string.Empty);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string Format(LogEntry entry) =>
        $"[{entry.Timestamp:dd.MM HH:mm:ss}] [{entry.Component}] [{entry.Severity}] {entry.Message}";
}
