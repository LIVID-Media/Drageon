using System.Diagnostics;
using System.IO;

namespace Drageon.App;

internal static class PowerShellLauncher
{
    public static async Task<string> OpenAsync(AppServices services, string? workingDirectory = null)
    {
        workingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
            ? services.Paths.AppRoot
            : Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(workingDirectory))
            throw new DirectoryNotFoundException(string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                Localization.Get("powershell.workingDirectoryMissing"), workingDirectory));

        await services.Packages.RefreshEnvironmentAsync();
        var active = await services.Store.ReadAsync<Dictionary<string, string>>(
            services.Paths.Resolve("app", "config", "packages.active.json")) ?? [];
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        startInfo.Environment["DRAGEON_ROOT"] = services.Paths.AppRoot;
        string message;
        if (active.TryGetValue("php", out var version))
        {
            var phpRoot = services.Paths.Resolve("bin", "php", version);
            var phpExecutable = Path.Combine(phpRoot, "php.exe");
            if (!File.Exists(phpExecutable)) throw new FileNotFoundException(Localization.Get("powershell.phpMissing"), phpExecutable);
            var generator = services.Generators.FirstOrDefault(static item => item.ComponentId == "php")
                ?? throw new InvalidOperationException(Localization.Get("powershell.generatorMissing"));
            var iniPath = await generator.GenerateAsync();
            startInfo.Environment["PATH"] = phpRoot + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? "");
            startInfo.Environment["PHPRC"] = Path.GetDirectoryName(iniPath)!;
            startInfo.Environment["DRAGEON_PHP_INI"] = iniPath;
            startInfo.Arguments = "-NoExit -NoLogo -ExecutionPolicy Bypass -Command \"$Host.UI.RawUI.WindowTitle = 'Drageon PowerShell'; php --ini\"";
            message = string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("powershell.openedWithPhp"), version);
        }
        else
        {
            startInfo.Arguments = "-NoExit -NoLogo -ExecutionPolicy Bypass -Command \"$Host.UI.RawUI.WindowTitle = 'Drageon PowerShell'\"";
            message = Localization.Get("powershell.openedWithoutPhp");
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(Localization.Get("powershell.startFailed"));
        return message;
    }
}
