using Drageon.Core;

namespace Drageon.Infrastructure;

/// <summary>Stores optional project metadata. Project existence is always determined by www/.</summary>
public sealed class ProjectMetadataStore(IPortablePathService paths, JsonFileStore store)
{
    private readonly string _path = paths.Resolve("app", "config", "projects.json");
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<IReadOnlyDictionary<string, ProjectMetadata>> ReadAndPruneAsync(CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var metadata = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var changed = false;
            foreach (var key in metadata.Keys.ToArray())
            {
                if (!TryNormalizeProjectPath(key, out var normalized)
                    || !Directory.Exists(ToAbsolutePath(normalized!))
                    || metadata[key].IsEmpty)
                {
                    metadata.Remove(key);
                    changed = true;
                    continue;
                }
                if (!key.Equals(normalized, StringComparison.Ordinal))
                {
                    metadata[normalized!] = metadata[key];
                    metadata.Remove(key);
                    changed = true;
                }
            }
            if (changed) await store.WriteAsync(_path, metadata, cancellationToken).ConfigureAwait(false);
            return new Dictionary<string, ProjectMetadata>(metadata, StringComparer.OrdinalIgnoreCase);
        }
        finally { Gate.Release(); }
    }

    public async Task<ProjectMetadata?> GetAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeExistingProjectPath(projectPath);
        var all = await ReadAndPruneAsync(cancellationToken).ConfigureAwait(false);
        return all.GetValueOrDefault(normalized);
    }

    public async Task SetAsync(string projectPath, ProjectMetadata metadata, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeExistingProjectPath(projectPath);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (metadata.IsEmpty) all.Remove(normalized);
            else all[normalized] = NormalizeMetadata(metadata);
            await store.WriteAsync(_path, all, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task UpdateAsync(string projectPath, Func<ProjectMetadata, ProjectMetadata> update, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeExistingProjectPath(projectPath);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            var value = NormalizeMetadata(update(all.GetValueOrDefault(normalized) ?? new ProjectMetadata()));
            if (value.IsEmpty) all.Remove(normalized); else all[normalized] = value;
            await store.WriteAsync(_path, all, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task RemoveAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeProjectPath(projectPath);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (all.Remove(normalized)) await store.WriteAsync(_path, all, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    public async Task AssignVirtualHostAsync(string? oldProjectPath, string? newProjectPath, string oldHost, string newHost, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            foreach (var key in all.Keys.ToArray())
            {
                if ((!string.IsNullOrWhiteSpace(oldProjectPath) && key.Equals(NormalizeProjectPath(oldProjectPath), StringComparison.OrdinalIgnoreCase))
                    || all[key].Vhost?.Equals(oldHost, StringComparison.OrdinalIgnoreCase) == true)
                {
                    var cleaned = all[key] with { Vhost = null };
                    if (cleaned.IsEmpty) all.Remove(key); else all[key] = cleaned;
                }
            }
            if (!string.IsNullOrWhiteSpace(newProjectPath))
            {
                var key = NormalizeExistingProjectPath(newProjectPath);
                all[key] = NormalizeMetadata((all.GetValueOrDefault(key) ?? new ProjectMetadata()) with { Vhost = newHost });
            }
            await store.WriteAsync(_path, all, cancellationToken).ConfigureAwait(false);
        }
        finally { Gate.Release(); }
    }

    private async Task<Dictionary<string, ProjectMetadata>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        var value = await store.ReadAsync<Dictionary<string, ProjectMetadata>>(_path, cancellationToken).ConfigureAwait(false);
        return value is null ? new(StringComparer.OrdinalIgnoreCase) : new(value, StringComparer.OrdinalIgnoreCase);
    }

    private static ProjectMetadata NormalizeMetadata(ProjectMetadata value) => value with
    {
        Name = NullIfWhiteSpace(value.Name), Database = NullIfWhiteSpace(value.Database), Url = NullIfWhiteSpace(value.Url),
        Vhost = NullIfWhiteSpace(value.Vhost), StagingOf = NullIfWhiteSpace(value.StagingOf)?.Replace('\\', '/')
    };

    private string NormalizeExistingProjectPath(string value)
    {
        var normalized = NormalizeProjectPath(value);
        if (!Directory.Exists(ToAbsolutePath(normalized))) throw new DirectoryNotFoundException($"Projektordner nicht gefunden: {normalized}");
        return normalized;
    }

    public string NormalizeProjectPath(string value)
    {
        if (!TryNormalizeProjectPath(value, out var normalized)) throw new ArgumentException("Projektpfad muss direkt unter www/ liegen.", nameof(value));
        return normalized!;
    }

    private bool TryNormalizeProjectPath(string value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var candidate = Path.IsPathRooted(value) ? Path.GetFullPath(value) : Path.GetFullPath(Path.Combine(paths.AppRoot, value.Replace('/', Path.DirectorySeparatorChar)));
        var www = paths.Resolve("www");
        var relative = Path.GetRelativePath(www, candidate);
        if (Path.IsPathRooted(relative) || relative is "." or ".." || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || relative.Contains(Path.DirectorySeparatorChar)) return false;
        normalized = $"www/{relative.Replace('\\', '/')}";
        return true;
    }

    private string ToAbsolutePath(string relativePath) => paths.Resolve(relativePath.Split('/'));
    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
