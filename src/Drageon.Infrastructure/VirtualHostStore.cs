using System.Text.RegularExpressions;
using Drageon.Core;

namespace Drageon.Infrastructure;

// Webserver-independent project data.
public sealed class VirtualHostStore(IPortablePathService paths, JsonFileStore store)
{
    public string DirectoryPath => paths.Resolve("app", "config", "vhosts");
    public async Task<IReadOnlyList<VirtualHostDefinition>> ReadAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(DirectoryPath);
        var hosts = new List<VirtualHostDefinition>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json").Order())
        {
            var host = await store.ReadAsync<VirtualHostDefinition>(file, cancellationToken);
            if (host is not null) hosts.Add(Resolve(host));
        }
        return hosts;
    }

    public VirtualHostDefinition Resolve(VirtualHostDefinition host)
    {
        if (host.Port is < 1 or > 65535 || !Regex.IsMatch(host.Host, @"^[a-zA-Z0-9][a-zA-Z0-9.-]*$"))
            throw new InvalidDataException("Invalid virtual host / port: " + host.Host);
        var root = host.DocumentRoot.Replace("${APPROOT}", paths.AppRoot, StringComparison.OrdinalIgnoreCase)
            .Replace("%APPROOT%", paths.AppRoot, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(root) || root.IndexOfAny(['\r', '\n', '"', '$', ';', '{', '}']) >= 0)
            throw new InvalidDataException("Invalid DocumentRoot: " + root);
        return host with { DocumentRoot = Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(paths.AppRoot, root)) };
    }
}

