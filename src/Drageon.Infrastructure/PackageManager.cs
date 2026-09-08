using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed class PackageManager(
    IPortablePathService paths,
    JsonFileStore store,
    ILanguageService? language = null,
    HttpClient? httpClient = null) : IPackageManager
{
    private readonly HttpClient _httpClient = httpClient ?? CreateHttpClient();
    private readonly string _activePackagesPath = paths.Resolve("app", "config", "packages.active.json");
    private readonly string _inheritedPath = Environment.GetEnvironmentVariable("PATH") ?? "";
    private readonly SemaphoreSlim _environmentLock = new(1, 1);

    public async Task RefreshEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        await _environmentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var manifests = await GetManifestsAsync(cancellationToken).ConfigureAwait(false);
            var installed = await GetInstalledAsync(cancellationToken).ConfigureAwait(false);
            var portablePath = PortableEnvironment.CreatePath(manifests, installed, _inheritedPath);
            Environment.SetEnvironmentVariable("PATH", portablePath, EnvironmentVariableTarget.Process);
        }
        finally
        {
            _environmentLock.Release();
        }
    }

    public async Task<IReadOnlyList<PackageManifest>> GetManifestsAsync(CancellationToken cancellationToken = default)
    {
        var manifests = new List<PackageManifest>();
        foreach (var file in Directory.EnumerateFiles(paths.Resolve("app", "packages"), "*.json"))
        {
            await using var stream = File.OpenRead(file);
            var manifest = await JsonSerializer.DeserializeAsync<PackageManifest>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            }, cancellationToken).ConfigureAwait(false);

            if (manifest is not null)
            {
                manifests.Add(manifest);
            }
        }

        return manifests.OrderBy(static item => item.DisplayName).ToArray();
    }

    public async Task<IReadOnlyList<InstalledPackage>> GetInstalledAsync(CancellationToken cancellationToken = default)
    {
        var active = await ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<InstalledPackage>();

        foreach (var manifest in await GetManifestsAsync(cancellationToken).ConfigureAwait(false))
        {
            var packageDirectory = paths.Resolve("bin", SafeSegment(manifest.InstallDirectory.Length == 0 ? manifest.Id : manifest.InstallDirectory));
            if (!Directory.Exists(packageDirectory))
            {
                continue;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(packageDirectory))
            {
                var version = Path.GetFileName(versionDirectory);
                result.Add(new InstalledPackage(manifest.Id, version, versionDirectory,
                    active.TryGetValue(manifest.Id, out var activeVersion) && activeVersion == version));
            }
        }

        return result;
    }

    public async Task InstallAsync(PackageManifest manifest, PackageRelease release, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var packageDirectory = SafeSegment(manifest.InstallDirectory.Length == 0 ? manifest.Id : manifest.InstallDirectory);
        var version = SafeSegment(release.Version);
        var targetDirectory = paths.Resolve("bin", packageDirectory, version);
        if (Directory.Exists(targetDirectory))
        {
            throw new InvalidOperationException(TF("package.error.alreadyInstalled", "{0} {1} ist bereits installiert.", manifest.DisplayName, release.Version));
        }

        var archiveType = release.ArchiveType.Trim().ToLowerInvariant();
        if (archiveType is not ("zip" or "file"))
        {
            throw new NotSupportedException(TF("package.error.archiveType", "Der Downloadtyp '{0}' wird noch nicht unterstützt.", release.ArchiveType));
        }
        var fileName = archiveType == "file"
            ? SafeSegment(string.IsNullOrWhiteSpace(release.FileName)
                ? Path.GetFileName(new Uri(release.Url).LocalPath)
                : release.FileName.Trim())
            : null;
        var downloadPath = paths.Resolve("app", "var", "tmp", "downloads", $"{manifest.Id}-{version}.download");
        var extractDirectory = paths.Resolve("app", "var", "tmp", "extract", $"{manifest.Id}-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.GetDirectoryName(downloadPath)!);
        Directory.CreateDirectory(extractDirectory);

        try
        {
            using var response = await DownloadWithRetryAsync(release.Url, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(downloadPath))
            {
                var buffer = new byte[81920];
                long downloaded = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    downloaded += count;
                    if (total > 0)
                    {
                        progress?.Report((double)downloaded / total.Value * 0.8);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(release.Sha256))
            {
                var actualHash = await ComputeSha256Async(downloadPath, cancellationToken).ConfigureAwait(false);
                if (!actualHash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(T("package.error.sha256", "Die SHA256-Prüfsumme des Downloads stimmt nicht mit dem Paketmanifest überein."));
                }
            }

            string sourceDirectory;
            if (archiveType == "file")
            {
                File.Move(downloadPath, Path.Combine(extractDirectory, fileName!));
                if (Path.GetExtension(fileName!).Equals(".phar", StringComparison.OrdinalIgnoreCase))
                {
                    var launcherPath = Path.Combine(extractDirectory, Path.ChangeExtension(fileName!, ".cmd"));
                    // Resolve the PHAR relative to the launcher, so moving Drageon keeps it usable.
                    await File.WriteAllTextAsync(launcherPath,
                        "@echo off\r\nsetlocal DisableDelayedExpansion\r\nphp \"%~dpn0.phar\" %*\r\nexit /b %errorlevel%\r\n",
                        cancellationToken).ConfigureAwait(false);
                }
                sourceDirectory = extractDirectory;
            }
            else
            {
                ZipFile.ExtractToDirectory(downloadPath, extractDirectory);
                sourceDirectory = GetArchiveSourceDirectory(extractDirectory, release);
            }

            if (!Directory.Exists(sourceDirectory))
            {
                throw new InvalidDataException(T("package.error.rootMissing", "Das erwartete Stammverzeichnis fehlt im Paketarchiv."));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetDirectory)!);
            Directory.Move(sourceDirectory, targetDirectory);
            progress?.Report(1);
        }
        finally
        {
            TryDeleteFile(downloadPath);
            TryDeleteDirectory(extractDirectory);
        }
    }

    public async Task ActivateAsync(string packageId, string version, CancellationToken cancellationToken = default)
    {
        packageId = SafeSegment(packageId);
        version = SafeSegment(version);
        var manifest = (await GetManifestsAsync(cancellationToken).ConfigureAwait(false)).Single(item => item.Id == packageId);
        var directory = paths.Resolve("bin", SafeSegment(manifest.InstallDirectory.Length == 0 ? manifest.Id : manifest.InstallDirectory), version);
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(TF("package.error.versionMissing", "Die Version {0} von {1} ist nicht installiert.", version, packageId));
        }

        var active = await ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(manifest.ExclusiveGroup))
        {
            foreach (var groupedPackage in (await GetManifestsAsync(cancellationToken).ConfigureAwait(false))
                         .Where(item => !item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(item.ExclusiveGroup, manifest.ExclusiveGroup, StringComparison.OrdinalIgnoreCase)))
            {
                active.Remove(groupedPackage.Id);
            }
        }
        active[packageId] = version;
        await store.WriteAsync(_activePackagesPath, active, cancellationToken).ConfigureAwait(false);
        await RefreshEnvironmentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeactivateAsync(string packageId, CancellationToken cancellationToken = default)
    {
        packageId = SafeSegment(packageId);
        var active = await ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active.Remove(packageId))
        {
            await store.WriteAsync(_activePackagesPath, active, cancellationToken).ConfigureAwait(false);
        }
        await RefreshEnvironmentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string packageId, string version, CancellationToken cancellationToken = default)
    {
        packageId = SafeSegment(packageId);
        version = SafeSegment(version);
        var manifest = (await GetManifestsAsync(cancellationToken).ConfigureAwait(false)).Single(item => item.Id == packageId);
        var active = await ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active.TryGetValue(packageId, out var activeVersion) && activeVersion == version)
        {
            throw new InvalidOperationException(T("package.error.deleteActive", "Eine aktive Paketversion kann nicht gelöscht werden."));
        }

        var directory = paths.Resolve("bin", SafeSegment(manifest.InstallDirectory.Length == 0 ? manifest.Id : manifest.InstallDirectory), version);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }

        DeleteVersionDirectory(paths.Resolve("app", "config", packageId, version));
        DeleteVersionDirectory(paths.Resolve("app", "var", "generated", packageId, version));
        if (packageId == "php")
        {
            DeleteVersionDirectory(paths.Resolve("app", "var", "tmp", ".opcache", version));
        }
        else if (packageId == "phpmyadmin")
        {
            DeleteVersionDirectory(paths.Resolve("app", "var", "tmp", "phpmyadmin", version));
        }
        await RefreshEnvironmentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteComponentAsync(string packageId, CancellationToken cancellationToken = default)
    {
        packageId = SafeSegment(packageId);
        var manifests = await GetManifestsAsync(cancellationToken).ConfigureAwait(false);
        var manifest = manifests.Single(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
        if (manifest.Bundled)
        {
            throw new InvalidOperationException(T("package.error.deleteBuiltIn", "Mitgelieferte Paketdefinitionen können nicht gelöscht werden."));
        }
        var active = await ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        if (active.ContainsKey(packageId))
        {
            throw new InvalidOperationException(T("package.error.deleteActiveComponent", "Eine aktive Komponente muss vor dem Löschen deaktiviert werden."));
        }

        if ((await GetInstalledAsync(cancellationToken).ConfigureAwait(false))
            .Any(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(T("package.error.deleteInstalledComponent", "Entferne zuerst alle installierten Versionen dieser Komponente."));
        }

        var installDirectory = SafeSegment(manifest.InstallDirectory.Length == 0 ? manifest.Id : manifest.InstallDirectory);
        if (manifests.Any(item => !item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.InstallDirectory.Length == 0 ? item.Id : item.InstallDirectory, installDirectory, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(T("package.error.sharedDirectory", "Das Installationsverzeichnis wird von einer weiteren Komponente verwendet."));
        }

        var componentDirectory = paths.Resolve("bin", installDirectory);
        if (Directory.Exists(componentDirectory))
        {
            Directory.Delete(componentDirectory, true);
        }

        var manifestFile = paths.Resolve("app", "packages", packageId + ".json");
        if (File.Exists(manifestFile))
        {
            File.Delete(manifestFile);
        }
        await RefreshEnvironmentAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, string>> ReadActiveAsync(CancellationToken cancellationToken) =>
        await store.ReadAsync<Dictionary<string, string>>(_activePackagesPath, cancellationToken).ConfigureAwait(false)
        ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private string SafeSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\'))
        {
            throw new InvalidDataException(TF("package.error.pathSegment", "Ungültiger Pfadbestandteil: '{0}'.", value));
        }

        return value;
    }

    private string GetArchiveSourceDirectory(string extractDirectory, PackageRelease release)
    {
        if (!string.IsNullOrWhiteSpace(release.StripPrefix))
        {
            return Path.Combine(extractDirectory, SafeSegment(release.StripPrefix.Trim()));
        }

        var archiveName = Path.GetFileNameWithoutExtension(new Uri(release.Url).LocalPath);
        if (!string.IsNullOrWhiteSpace(archiveName))
        {
            var matchingDirectory = Path.Combine(extractDirectory, SafeSegment(archiveName));
            if (Directory.Exists(matchingDirectory)) return matchingDirectory;
        }

        // Archives such as PHP's ZIPs contain their files directly at the root.
        return extractDirectory;
    }

    private static async Task<string> ComputeSha256Async(string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { }
    }

    private static void DeleteVersionDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private async Task<HttpResponseMessage> DownloadWithRetryAsync(string url, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url)
                {
                    Version = HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };
                var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                try { response.EnsureSuccessStatusCode(); }
                catch { response.Dispose(); throw; }
                return response;
            }
            catch (Exception exception) when ((exception is HttpRequestException or IOException) && attempt < 3)
            {
                lastError = exception;
                await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                lastError = exception;
                break;
            }
        }

        throw new InvalidOperationException(TF("package.error.download", "Download fehlgeschlagen: {0}", FlattenMessage(lastError)), lastError);
    }

    private string FlattenMessage(Exception? exception)
    {
        if (exception is null) return T("package.error.networkUnknown", "Unbekannter Netzwerkfehler.");
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!messages.Contains(current.Message, StringComparer.Ordinal)) messages.Add(current.Message);
        }
        return string.Join(" — ", messages);
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Drageon/1.0.0 (+portable Windows development environment)");
        return client;
    }

    private string T(string key, string fallback) => language?.Get(key, fallback) ?? fallback;
    private string TF(string key, string fallback, params object?[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, T(key, fallback), arguments);
}
