using Drageon.Core;

namespace Drageon.Infrastructure;

public static class VersionedComponentConfiguration
{
    public static async Task<string?> GetActiveVersionAsync(
        IPortablePathService paths,
        JsonFileStore store,
        string componentId,
        CancellationToken cancellationToken = default)
    {
        componentId = ValidateSegment(componentId, nameof(componentId));
        var active = await store.ReadAsync<Dictionary<string, string>>(
            paths.Resolve("app", "config", "packages.active.json"), cancellationToken).ConfigureAwait(false);
        return active?.GetValueOrDefault(componentId);
    }

    public static string GetSettingsPath(IPortablePathService paths, string componentId, string version) =>
        paths.Resolve("app", "config", ValidateSegment(componentId, nameof(componentId)), ValidateSegment(version, nameof(version)), "settings.json");

    public static string GetGeneratedDirectory(IPortablePathService paths, string componentId, string version) =>
        paths.Resolve("app", "var", "generated", ValidateSegment(componentId, nameof(componentId)), ValidateSegment(version, nameof(version)));

    public static string GetDataDirectory(IPortablePathService paths, string componentId, string version) =>
        paths.Resolve("data", ValidateSegment(componentId, nameof(componentId)), ValidateSegment(version, nameof(version)));

    public static async Task<T> LoadAsync<T>(
        IPortablePathService paths,
        JsonFileStore store,
        string componentId,
        string version,
        Func<T> createDefault,
        CancellationToken cancellationToken = default)
    {
        var settingsPath = GetSettingsPath(paths, componentId, version);
        var settings = await store.ReadAsync<T>(settingsPath, cancellationToken).ConfigureAwait(false);
        if (settings is not null) return settings;

        settings = createDefault();
        await store.WriteAsync(settingsPath, settings, cancellationToken).ConfigureAwait(false);
        return settings;
    }

    private static string ValidateSegment(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Path.GetFileName(value).Equals(value, StringComparison.Ordinal)
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("Ungültiger Pfadbestandteil.", parameterName);
        }

        return value;
    }
}
