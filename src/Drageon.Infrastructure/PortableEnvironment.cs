using Drageon.Core;

namespace Drageon.Infrastructure;

public static class PortableEnvironment
{
    public static bool IsValidPathDirectory(string value)
    {
        if (Path.IsPathRooted(value) || value.Contains(';')) return false;
        return value.Split(['/', '\\']).All(segment =>
            segment != ".." && segment.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
    }

    public static string CreatePath(IEnumerable<PackageManifest> manifests,
        IEnumerable<InstalledPackage> installed, string inheritedPath)
    {
        var activePackages = installed.Where(package => package.IsActive)
            .ToDictionary(package => package.Id, StringComparer.OrdinalIgnoreCase);
        var directories = new List<string>();
        foreach (var manifest in manifests.Where(manifest => manifest.AddToPath))
        {
            if (!activePackages.TryGetValue(manifest.Id, out var package)) continue;

            var root = Path.GetFullPath(package.Directory);
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            var relativeDirectories = manifest.PathDirectories.Count == 0 ? ["."] : manifest.PathDirectories;
            foreach (var relativeDirectory in relativeDirectories)
            {
                // Invalid entries in hand-edited manifests must not add directories outside the package.
                if (!IsValidPathDirectory(relativeDirectory)) continue;
                var directory = Path.GetFullPath(Path.Combine(root, relativeDirectory));
                if (!directory.Equals(root, StringComparison.OrdinalIgnoreCase)
                    && !directory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (Directory.Exists(directory)) directories.Add(directory);
            }
        }

        var portableDirectories = directories.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return portableDirectories.Length == 0 ? inheritedPath
            : string.Join(Path.PathSeparator, portableDirectories)
                + (inheritedPath.Length == 0 ? "" : Path.PathSeparator + inheritedPath);
    }
}
