using System.Diagnostics;
using System.Text.Json;

namespace Drageon.Infrastructure;

public static class PhpExtensionProbe
{
    public static async Task<HashSet<string>?> GetLoadableAsync(
        string phpRoot,
        IReadOnlyList<string> extensions,
        CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(phpRoot, "php.exe");
        var extensionDirectory = Path.Combine(phpRoot, "ext");
        if (!File.Exists(executable) || !Directory.Exists(extensionDirectory)) return null;

        var info = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = phpRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        info.ArgumentList.Add("-n");
        info.ArgumentList.Add("-d");
        info.ArgumentList.Add($"extension_dir={extensionDirectory}");
        foreach (var extension in extensions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var expectedDll = extension.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? extension : $"php_{extension}.dll";
            if (!File.Exists(Path.Combine(extensionDirectory, expectedDll))) continue;
            info.ArgumentList.Add("-d");
            info.ArgumentList.Add($"extension={extension}");
        }
        info.ArgumentList.Add("-r");
        info.ArgumentList.Add("echo json_encode(get_loaded_extensions());");

        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var jsonStart = output.LastIndexOf("[", StringComparison.Ordinal);
            var jsonEnd = output.LastIndexOf(']');
            if (jsonStart < 0 || jsonEnd < jsonStart) return null;
            var loaded = JsonSerializer.Deserialize<string[]>(output[jsonStart..(jsonEnd + 1)]) ?? [];
            return loaded.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException
            or System.ComponentModel.Win32Exception or JsonException)
        {
            return null;
        }
    }

    public static string Normalize(string value)
    {
        var name = Path.GetFileNameWithoutExtension(value.Trim().Trim('"', '\''));
        return name.StartsWith("php_", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
    }
}
