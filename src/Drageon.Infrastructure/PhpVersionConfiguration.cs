using Drageon.Core;

namespace Drageon.Infrastructure;

public static class PhpVersionConfiguration
{
    public static string GetSettingsPath(IPortablePathService paths, string version) =>
        VersionedComponentConfiguration.GetSettingsPath(paths, "php", version);

    public static string GetGeneratedDirectory(IPortablePathService paths, string version) =>
        VersionedComponentConfiguration.GetGeneratedDirectory(paths, "php", version);

    public static string GetIniPath(IPortablePathService paths, string version) =>
        Path.Combine(GetGeneratedDirectory(paths, version), "php.ini");

    public static string GetOpcacheDirectory(IPortablePathService paths, string version)
    {
        _ = GetSettingsPath(paths, version);
        return paths.Resolve("app", "var", "tmp", ".opcache", version);
    }

    public static async Task<PhpSettings> LoadAsync(
        IPortablePathService paths,
        JsonFileStore store,
        string version,
        CancellationToken cancellationToken = default)
    {
        return await VersionedComponentConfiguration.LoadAsync(
            paths, store, "php", version, static () => new PhpSettings(), cancellationToken).ConfigureAwait(false);
    }
}
