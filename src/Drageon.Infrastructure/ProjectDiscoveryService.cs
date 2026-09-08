using System.Text.Json;
using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed class ProjectDiscoveryService(
    IPortablePathService paths,
    JsonFileStore store,
    ISettingsService settings) : IProjectDiscoveryService
{
    // Rules are evaluated from most specific to most general. Add a rule here to
    // support another framework without changing the scanner or the view models.
    private static readonly ProjectTypeRule[] DetectionRules =
    [
        new("WordPress", ["WordPress", "PHP"], context =>
            context.HasFile("wp-config.php") || context.HasDirectory("wp-content")),
        new("Contao / Symfony", ["Contao", "Symfony", "PHP", "Composer"], context =>
            context.HasFile("public", "contao-manager.phar.php") || context.RequiresComposerPackage("contao/")),
        new("Laravel", ["Laravel", "PHP", "Composer"], context =>
            context.HasFile("artisan") || context.RequiresComposerPackage("laravel/framework")),
        new("Symfony", ["Symfony", "PHP", "Composer"], context =>
            context.HasFile("symfony.lock") || context.HasFile("bin", "console") || context.RequiresComposerPackage("symfony/framework-bundle")),
        new("PHP / Composer", ["PHP", "Composer"], context => context.HasComposerFile),
        new("Node / Frontend", ["Node", "npm"], context => context.HasFile("package.json")),
        new("PHP", ["PHP"], context => context.HasFile("index.php") || context.HasFile("public", "index.php") || context.HasTopLevelFiles("*.php")),
        new("Static / HTML", ["Static", "HTML"], context =>
            context.HasFile("index.html") || context.HasFile("index.htm") || context.HasFile("public", "index.html")
            || context.HasTopLevelFiles("*.html") || context.HasTopLevelFiles("*.htm"))
    ];

    private readonly string _preferencesPath = paths.Resolve("app", "config", "projects.json");
    private readonly SemaphoreSlim _preferencesLock = new(1, 1);

    public async Task<IReadOnlyList<LocalProject>> GetProjectsAsync(CancellationToken cancellationToken = default)
    {
        var favorites = await ReadFavoritesAsync(cancellationToken).ConfigureAwait(false);
        var virtualHosts = await ReadVirtualHostsAsync(cancellationToken).ConfigureAwait(false);
        var wwwRoot = paths.Resolve("www");
        if (!Directory.Exists(wwwRoot)) return [];

        var projects = new List<LocalProject>();
        foreach (var directory in Directory.EnumerateDirectories(wwwRoot).OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(name) || name.StartsWith('.')) continue;

            var context = new ProjectDetectionContext(directory);
            var detection = DetectionRules.FirstOrDefault(rule => rule.Matches(context))
                ?? new ProjectTypeRule("Webprojekt", [], static _ => true);
            var virtualHost = virtualHosts.FirstOrDefault(host => IsPathWithin(directory, host.DocumentRoot));
            var id = name;
            var url = virtualHost is null ? CreateFolderUrl(name) : CreateVirtualHostUrl(virtualHost);
            projects.Add(new LocalProject(
                id,
                name,
                directory,
                url,
                detection.Type,
                detection.Technologies,
                favorites.Contains(id),
                virtualHost is not null));
        }

        return projects;
    }

    public async Task SetFavoriteAsync(string projectId, bool isFavorite, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(projectId) || Path.GetFileName(projectId) != projectId)
        {
            throw new ArgumentException("Ungültige Projekt-ID.", nameof(projectId));
        }

        await _preferencesLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var favorites = await ReadFavoritesAsync(cancellationToken).ConfigureAwait(false);
            if (isFavorite) favorites.Add(projectId);
            else favorites.Remove(projectId);

            await store.WriteAsync(
                _preferencesPath,
                new ProjectPreferences { Favorites = favorites.Order(StringComparer.CurrentCultureIgnoreCase).ToArray() },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _preferencesLock.Release();
        }
    }

    private async Task<HashSet<string>> ReadFavoritesAsync(CancellationToken cancellationToken)
    {
        var preferences = await store.ReadAsync<ProjectPreferences>(_preferencesPath, cancellationToken).ConfigureAwait(false)
            ?? new ProjectPreferences();
        return preferences.Favorites
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<VirtualHostMapping>> ReadVirtualHostsAsync(CancellationToken cancellationToken) =>
        (await new VirtualHostStore(paths, store).ReadAsync(cancellationToken: cancellationToken))
            .Where(host => host.Enabled)
            .Select(host => new VirtualHostMapping(host.Host, host.DocumentRoot, host.Port ?? settings.Current.HttpPort)).ToArray();
    private string CreateFolderUrl(string directoryName)
    {
        var port = settings.Current.HttpPort;
        return $"http://localhost{(port == 80 ? "" : $":{port}")}/{Uri.EscapeDataString(directoryName)}/";
    }

    private static string CreateVirtualHostUrl(VirtualHostMapping virtualHost)
    {
        if (Uri.TryCreate(virtualHost.ServerName, UriKind.Absolute, out var absolute)
            && (absolute.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || absolute.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))) return absolute.ToString();
        var scheme = virtualHost.Port == 443 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp;
        var host = virtualHost.ServerName;
        var explicitPort = host.LastIndexOf(':') > host.LastIndexOf(']');
        var port = explicitPort || virtualHost.Port is 80 or 443 ? "" : $":{virtualHost.Port}";
        return $"{scheme}://{host}{port}/";
    }

    private static bool IsPathWithin(string projectDirectory, string documentRoot)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(projectDirectory), Path.GetFullPath(documentRoot));
        return relative == "." || (!Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
    }

    private sealed record ProjectTypeRule(
        string Type,
        IReadOnlyList<string> Technologies,
        Func<ProjectDetectionContext, bool> Matches);

    private sealed record VirtualHostMapping(string ServerName, string DocumentRoot, int Port);

    private sealed class ProjectDetectionContext(string directory)
    {
        private JsonDocument? _composer;
        private bool _composerRead;

        public bool HasComposerFile => File.Exists(Path.Combine(directory, "composer.json"));

        public bool HasFile(params string[] segments) => File.Exists(segments.Aggregate(directory, Path.Combine));

        public bool HasDirectory(params string[] segments) => Directory.Exists(segments.Aggregate(directory, Path.Combine));

        public bool HasTopLevelFiles(string pattern) => Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly).Any();

        public bool RequiresComposerPackage(string packageNameOrPrefix)
        {
            var composer = GetComposer();
            if (composer is null) return false;
            foreach (var sectionName in new[] { "require", "require-dev" })
            {
                if (!composer.RootElement.TryGetProperty(sectionName, out var section) || section.ValueKind != JsonValueKind.Object) continue;
                if (section.EnumerateObject().Any(package => package.Name.Equals(packageNameOrPrefix, StringComparison.OrdinalIgnoreCase)
                    || package.Name.StartsWith(packageNameOrPrefix, StringComparison.OrdinalIgnoreCase))) return true;
            }
            return false;
        }

        private JsonDocument? GetComposer()
        {
            if (_composerRead) return _composer;
            _composerRead = true;
            var path = Path.Combine(directory, "composer.json");
            if (!File.Exists(path)) return null;
            try
            {
                _composer = JsonDocument.Parse(File.ReadAllText(path));
            }
            catch (JsonException)
            {
                _composer = null;
            }
            return _composer;
        }
    }
}


