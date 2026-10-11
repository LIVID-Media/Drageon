using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Drageon.Infrastructure;

internal static class OperationSafety
{
    // Internal fault seam used by the Windows regression executable, never exposed to the UI.
    internal static Action<string>? Checkpoint { get; set; }
    // Serializes snapshot/staging mutations, including their shared VHost and metadata resources.
    internal static readonly SemaphoreSlim Gate = new(1, 1);
    // Same-volume, exclusive rename. A transient managed/API refusal must not require a server restart.
    internal static void MoveDirectory(string source, string destination, CancellationToken token = default)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        NoLinks(source); NoLinks(destination);
        if (!string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase)) throw new IOException("Ordneraustausch muss auf demselben Volume erfolgen.");
        var identity = DirectoryIdentity(source);
        token.ThrowIfCancellationRequested();
        try
        {
            Checkpoint?.Invoke("directory-rename");
            Directory.Move(source, destination);
            return;
        }
        catch (IOException error) when ((error.HResult & 0xFFFF) is 5 or 32) { }
        catch (UnauthorizedAccessException) { }
        for (var attempt = 0; attempt < 4; attempt++)
        {
            token.ThrowIfCancellationRequested();
            NoLinks(source); NoLinks(destination);
            if (DirectoryIdentity(source) != identity) throw new IOException("Quellordner wurde zwischenzeitlich ersetzt; fremder Bestand bleibt erhalten.");
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Umbenennungsziel existiert bereits; Bestand bleibt erhalten.");
            // No REPLACE_EXISTING, COPY_ALLOWED or POSIX flags: never replace another target or bypass sharing locks.
            if (MoveFileEx(Extended(source), Extended(destination), 0)) return;
            var code = Marshal.GetLastWin32Error();
            if (code is not (5 or 32) || attempt == 3) throw new IOException($"Ordner konnte nicht umbenannt werden: {source}; Ziel: {destination}; Windows-Fehler {code}: {new System.ComponentModel.Win32Exception(code).Message}", unchecked((int)(0x80070000u | (uint)code)));
            Thread.Sleep(100);
        }
    }
    private static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, uint flags);
    internal static void CreateDirectoryExclusive(string path)
    {
        NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!CreateDirectory(path, IntPtr.Zero)) throw new IOException($"Ordner konnte nicht exklusiv erstellt werden: {path}", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr security);
    internal static bool IsOperationDirectory(string path) => new[] { ".prepare-", ".previous-", ".delete-", ".recovery-" }.Any(marker => Path.GetFileName(path).Contains(marker, StringComparison.Ordinal));
    internal static void AssertNoPending(string target)
    {
        var parent = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(parent)) return;
        foreach (var journal in Directory.EnumerateFiles(parent, Path.GetFileName(target) + ".*operation-*.json"))
        {
            NoLinks(journal);
            using var document = JsonDocument.Parse(File.ReadAllText(journal));
            if (!document.RootElement.TryGetProperty("Phase", out var phase) || phase.GetString() is not ("completed" or "rolled-back"))
                throw new IOException($"Unverändert abgebrochen: Unterbrochene Operation zuerst wiederherstellen: {journal}");
        }
    }
    internal static bool Contains(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) || fullPath.StartsWith(Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException($"Unerwarteter Link im Operationspfad: {current}");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static string DirectoryIdentity(string path)
    {
        NoLinks(path);
        using var handle = OpenDirectory(path, 0x80, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandleEx(handle, 18, out var information, (uint)Marshal.SizeOf<DirectoryInformation>()) || (information.IdLow == 0 && information.IdHigh == 0)) throw new IOException($"Verlässliche Ordneridentität wird auf diesem Dateisystem nicht unterstützt: {path}");
        return $"{information.VolumeSerial:X16}:{information.IdHigh:X16}{information.IdLow:X16}";
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct DirectoryInformation
    {
        public ulong VolumeSerial, IdLow, IdHigh;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string path, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, out DirectoryInformation information, uint size);

    internal static string Relative(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains('\\')) throw new InvalidDataException($"Ungültiger relativer Pfad: {value}");
        foreach (var part in value.Split('/'))
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3])))
                throw new InvalidDataException($"Ungültiger Pfadname: {value}");
        }
        return value;
    }

    internal static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void Persist<T>(string path, T state)
    {
        NoLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, state);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}

// The journal is written before the first rename. Backups remain until explicit recovery/cleanup.
internal sealed record FileExchange(string Target, string Prepared, string Previous, bool HadPrevious, string Phase, string? PreviousIdentity = null, string? PreparedIdentity = null);
internal sealed record DatabaseStorageIdentity(string DataDirectory, string DataDirectoryIdentity, string SystemDirectoryIdentity, string DatabaseDirectoryIdentity);
internal sealed record StagingOwnership(string Identity, string ProjectPath, string? VhostPath, string? VhostHash, string? CreatedDatabase = null, string? ServerIdentity = null, string? RootIdentity = null, DatabaseStorageIdentity? DatabaseIdentity = null);
internal sealed record StagingDeletion(string Target, string Quarantine, string Recovery, string? Vhost, IReadOnlyList<Drageon.Core.ProjectSnapshotInfo> Snapshots, string Phase, string? RecoveryIdentity = null, string? Database = null, string? ServerIdentity = null, DatabaseStorageIdentity? DatabaseIdentity = null, string? DatabaseSha256 = null, string? CharacterSet = null, string? Collation = null);
internal sealed record DatabaseRecovery(string Database, string ServerIdentity, string? Backup, string Phase, string? CharacterSet = null, string? Collation = null, string? BackupSha256 = null, string? Input = null);
