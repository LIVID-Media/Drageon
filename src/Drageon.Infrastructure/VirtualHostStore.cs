using System.Text.RegularExpressions;
using Drageon.Core;

namespace Drageon.Infrastructure;

// Webserver-independent project data.
public sealed class VirtualHostStore(IPortablePathService paths, JsonFileStore store)
{
    public async Task<string> SaveAsync(VirtualHostDefinition host, string? oldPath, string? oldProject, string? newProject, string? oldHost, CancellationToken cancellationToken = default)
    {
        await OperationSafety.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Resolve(host);
            var path = Path.Combine(DirectoryPath, host.Host.ToLowerInvariant() + ".json");
            ValidatePath(path);
            if (oldPath is not null) ValidatePath(oldPath);
            if (File.Exists(path) && !path.Equals(oldPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Virtual Host existiert bereits.");
            await store.WriteAsync(path, host, cancellationToken, overwrite: path.Equals(oldPath, StringComparison.OrdinalIgnoreCase)).ConfigureAwait(false);
            if (oldPath is not null && !oldPath.Equals(path, StringComparison.OrdinalIgnoreCase) && File.Exists(oldPath)) File.Delete(oldPath);
            await new ProjectMetadataStore(paths, store).AssignVirtualHostWithinOperationAsync(oldProject, newProject, oldHost ?? host.Host, host.Host, cancellationToken).ConfigureAwait(false);
            return path;
        }
        finally { OperationSafety.Gate.Release(); }
    }

    public async Task DeleteAsync(string path, string host, string? project, CancellationToken cancellationToken = default)
    {
        await OperationSafety.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ValidatePath(path);
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
            await new ProjectMetadataStore(paths, store).AssignVirtualHostWithinOperationAsync(project, null, host, host, cancellationToken).ConfigureAwait(false);
        }
        finally { OperationSafety.Gate.Release(); }
    }

    private void ValidatePath(string path)
    {
        if (!Path.GetDirectoryName(Path.GetFullPath(path))!.Equals(DirectoryPath, StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path) != ".json") throw new InvalidDataException("Ungültiger VHost-Pfad.");
        OperationSafety.NoLinks(path);
    }

    public string DirectoryPath => paths.Resolve("app", "config", "vhosts");
    public async Task<IReadOnlyList<VirtualHostDefinition>> ReadAsync(CancellationToken cancellationToken = default)
    {
        OperationSafety.NoLinks(DirectoryPath);
        Directory.CreateDirectory(DirectoryPath);
        var hosts = new List<VirtualHostDefinition>();
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "*.json").Order())
        {
            OperationSafety.NoLinks(file);
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
