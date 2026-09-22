using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Drageon.Core;

namespace Drageon.Infrastructure;

public static class DatabaseCredentialProtector
{
    private const string PortablePrefix = "portable-v1:";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int LegacyFlags = 0x1 | 0x4;
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("Drageon.DatabaseCredentials.v1");
    private static readonly object KeyGate = new();

    public static string Protect(string password, IPortablePathService paths)
    {
        if (string.IsNullOrEmpty(password)) return "";
        var key = LoadOrCreateKey(paths);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plaintext = Encoding.UTF8.GetBytes(password);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize)) aes.Encrypt(nonce, plaintext, ciphertext, tag);
        var payload = new byte[NonceSize + TagSize + ciphertext.Length];
        nonce.CopyTo(payload, 0); tag.CopyTo(payload, NonceSize); ciphertext.CopyTo(payload, NonceSize + TagSize);
        CryptographicOperations.ZeroMemory(plaintext);
        return PortablePrefix + Convert.ToBase64String(payload);
    }

    public static string? Unprotect(string protectedPassword, IPortablePathService paths)
    {
        if (string.IsNullOrWhiteSpace(protectedPassword)) return null;
        if (!protectedPassword.StartsWith(PortablePrefix, StringComparison.Ordinal)) return UnprotectLegacy(protectedPassword);
        byte[] payload;
        try { payload = Convert.FromBase64String(protectedPassword[PortablePrefix.Length..]); }
        catch (FormatException exception) { throw new InvalidDataException("Das gespeicherte Datenbankpasswort ist ungültig.", exception); }
        if (payload.Length < NonceSize + TagSize) throw new InvalidDataException("Das gespeicherte Datenbankpasswort ist unvollständig.");
        var key = LoadKey(paths);
        var plaintext = new byte[payload.Length - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(payload.AsSpan(0, NonceSize), payload.AsSpan(NonceSize + TagSize), payload.AsSpan(NonceSize, TagSize), plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (AuthenticationTagMismatchException exception)
        {
            throw new InvalidDataException("Das gespeicherte Datenbankpasswort kann mit dem portablen Drageon-Schlüssel nicht entschlüsselt werden.", exception);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static bool IsPortable(string protectedPassword) => protectedPassword.StartsWith(PortablePrefix, StringComparison.Ordinal);

    private static byte[] LoadOrCreateKey(IPortablePathService paths)
    {
        lock (KeyGate)
        {
            var path = KeyPath(paths);
            if (File.Exists(path)) return ReadKey(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var key = RandomNumberGenerator.GetBytes(KeySize);
            var temporary = path + ".partial";
            try
            {
                File.WriteAllText(temporary, Convert.ToBase64String(key), new UTF8Encoding(false));
                File.Move(temporary, path, false);
                return key;
            }
            catch
            {
                try { File.Delete(temporary); } catch { }
                if (File.Exists(path)) return ReadKey(path);
                throw;
            }
        }
    }

    private static byte[] LoadKey(IPortablePathService paths)
    {
        var path = KeyPath(paths);
        if (!File.Exists(path)) throw new InvalidDataException("Der portable Schlüssel app/config/database-credentials.key fehlt. Bitte das Passwort in den Komponenteneinstellungen neu speichern.");
        return ReadKey(path);
    }

    private static byte[] ReadKey(string path)
    {
        try
        {
            var key = Convert.FromBase64String(File.ReadAllText(path).Trim());
            if (key.Length != KeySize) throw new InvalidDataException("Der portable Datenbank-Schlüssel besitzt eine ungültige Länge.");
            return key;
        }
        catch (FormatException exception) { throw new InvalidDataException("Der portable Datenbank-Schlüssel ist ungültig.", exception); }
    }

    private static string KeyPath(IPortablePathService paths) => paths.Resolve("app", "config", "database-credentials.key");

    private static string UnprotectLegacy(string protectedPassword)
    {
        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(protectedPassword); }
        catch (FormatException exception) { throw new InvalidDataException("Das gespeicherte Datenbankpasswort ist ungültig.", exception); }
        var input = Blob.FromBytes(encrypted); var entropy = Blob.FromBytes(LegacyEntropy);
        try
        {
            if (!CryptUnprotectData(ref input.Value, IntPtr.Zero, ref entropy.Value, IntPtr.Zero, IntPtr.Zero, LegacyFlags, out var output))
                throw new InvalidOperationException("Das ältere Datenbankpasswort kann nicht übernommen werden. Bitte speichern Sie es in den Komponenteneinstellungen erneut.", new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            try { return Encoding.UTF8.GetString(output.ToBytes()); }
            finally { if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
        }
        finally { input.Dispose(); entropy.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Size; public IntPtr Data; public readonly byte[] ToBytes() { var bytes = new byte[Size]; if (Size > 0) Marshal.Copy(Data, bytes, 0, Size); return bytes; } }
    private sealed class Blob : IDisposable
    {
        public DataBlob Value;
        public static Blob FromBytes(byte[] bytes) { var blob = new Blob { Value = new DataBlob { Size = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) } }; if (bytes.Length > 0) Marshal.Copy(bytes, 0, blob.Value.Data, bytes.Length); return blob; }
        public void Dispose() { if (Value.Data != IntPtr.Zero) { Marshal.FreeHGlobal(Value.Data); Value.Data = IntPtr.Zero; } }
    }
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
