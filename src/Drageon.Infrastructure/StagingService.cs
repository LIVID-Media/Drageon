using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed class StagingService(
    IPortablePathService paths,
    JsonFileStore store,
    IProjectDiscoveryService projects,
    DatabaseBackupService databases,
    IProjectSnapshotService snapshots,
    IAppLogService logs) : IStagingService
{
    public async Task<StagingResult> CreateAsync(StagingRequest request, DatabaseConnection? connection = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var metadataStore = new ProjectMetadataStore(paths, store);
        var sourceRelative = metadataStore.NormalizeProjectPath(request.SourceProjectPath);
        var source = paths.Resolve(sourceRelative.Split('/'));
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException($"Quellprojekt fehlt: {sourceRelative}");
        var targetRelative = NormalizeNewProjectPath(request.TargetProjectPath);
        var target = paths.Resolve(targetRelative.Split('/'));
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException($"Staging-Ziel existiert bereits: {targetRelative}");
        var sourceMetadata = await projects.GetMetadataAsync(sourceRelative, cancellationToken).ConfigureAwait(false) ?? new ProjectMetadata();
        if (request.CopyDatabase && string.IsNullOrWhiteSpace(sourceMetadata.Database)) throw new InvalidOperationException("Dem Quellprojekt ist keine Datenbank zugeordnet.");
        if (request.CopyDatabase && string.IsNullOrWhiteSpace(request.TargetDatabase)) throw new InvalidOperationException("Für die Staging-Kopie fehlt ein Datenbankname.");
        if (request.CopyDatabase && (await databases.GetDatabasesAsync(connection, cancellationToken).ConfigureAwait(false)).Contains(request.TargetDatabase!, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Die Datenbank {request.TargetDatabase} existiert bereits.");

        var temporary = paths.Resolve("app", "var", "tmp", $"staging-files-{Guid.NewGuid():N}");
        string? createdVhostPath = null; var databaseCreated = false;
        try
        {
            progress?.Report("files");
            var entries = SafeFileTree.Capture(source, temporary, cancellationToken);
            SafeFileTree.Restore(temporary, target, entries, logs, cancellationToken);
            if (request.CopyDatabase)
            {
                progress?.Report("database");
                await databases.CreateDatabaseFromAsync(sourceMetadata.Database!, request.TargetDatabase!, connection, cancellationToken).ConfigureAwait(false);
                databaseCreated = true;
            }

            string? vhostName = null;
            if (request.CreateVirtualHost)
            {
                progress?.Report("vhost");
                vhostName = GetHost(request.Url);
                var sourceVhost = await FindVirtualHostAsync(sourceMetadata.Vhost, cancellationToken).ConfigureAwait(false);
                var documentRoot = targetRelative;
                if (sourceVhost is not null)
                {
                    var resolvedSourceRoot = ResolveDocumentRoot(sourceVhost.DocumentRoot);
                    var suffix = Path.GetRelativePath(source, resolvedSourceRoot);
                    if (suffix == ".")
                        documentRoot = targetRelative;
                    else if (!Path.IsPathRooted(suffix) && suffix != ".." && !suffix.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                        documentRoot = Path.Combine(targetRelative, suffix).Replace('\\', '/');
                }
                var newVhost = sourceVhost is null
                    ? new VirtualHostDefinition(vhostName, documentRoot)
                    : sourceVhost with { Host = vhostName, DocumentRoot = documentRoot };
                createdVhostPath = Path.Combine(paths.Resolve("app", "config", "vhosts"), VHostFileName(vhostName));
                if (File.Exists(createdVhostPath)) throw new IOException($"Virtual Host {vhostName} existiert bereits.");
                await store.WriteAsync(createdVhostPath, newVhost, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report("metadata");
            await projects.SetMetadataAsync(targetRelative, new ProjectMetadata
            {
                Name = request.ProjectName.Trim(),
                Database = request.CopyDatabase ? request.TargetDatabase : null,
                Url = string.IsNullOrWhiteSpace(request.Url) ? null : request.Url.Trim(),
                Vhost = vhostName,
                StagingOf = sourceRelative
            }, cancellationToken).ConfigureAwait(false);
            logs.Write("Staging", $"Staging erstellt: {targetRelative}");
            return new(targetRelative, request.CopyDatabase ? request.TargetDatabase : null, request.Url);
        }
        catch
        {
            if (Directory.Exists(target)) SafeFileTree.Delete(target);
            if (createdVhostPath is not null) File.Delete(createdVhostPath);
            if (databaseCreated) try { await databases.DropDatabaseAsync(request.TargetDatabase!, connection, cancellationToken).ConfigureAwait(false); } catch (Exception exception) { logs.Write("Staging", exception.Message, LogSeverity.Error); }
            throw;
        }
        finally { if (Directory.Exists(temporary)) SafeFileTree.Delete(temporary); }
    }

    public async Task DeleteAsync(string stagingProjectPath, bool deleteDatabase, DatabaseConnection? connection = null, CancellationToken cancellationToken = default)
    {
        var metadataStore = new ProjectMetadataStore(paths, store);
        var relative = metadataStore.NormalizeProjectPath(stagingProjectPath);
        var metadata = await projects.GetMetadataAsync(relative, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Für dieses Projekt sind keine Staging-Metadaten vorhanden.");
        if (string.IsNullOrWhiteSpace(metadata.StagingOf)) throw new InvalidOperationException("Das ausgewählte Projekt ist kein von Drageon erzeugtes Staging-Projekt.");
        if (deleteDatabase && !string.IsNullOrWhiteSpace(metadata.Database)) await databases.DropDatabaseAsync(metadata.Database, connection, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(metadata.Vhost))
        {
            var vhost = Path.Combine(paths.Resolve("app", "config", "vhosts"), VHostFileName(metadata.Vhost));
            if (File.Exists(vhost)) File.Delete(vhost);
        }
        foreach (var snapshot in await snapshots.GetProjectSnapshotsAsync(relative, cancellationToken).ConfigureAwait(false))
            await snapshots.DeleteProjectSnapshotAsync(snapshot.DirectoryPath, cancellationToken).ConfigureAwait(false);
        SafeFileTree.Delete(paths.Resolve(relative.Split('/')));
        await metadataStore.RemoveAsync(relative, cancellationToken).ConfigureAwait(false);
        logs.Write("Staging", $"Staging gelöscht: {relative}");
    }

    private string NormalizeNewProjectPath(string value)
    {
        var candidate = value.Replace('\\', '/').Trim('/');
        if (!candidate.StartsWith("www/", StringComparison.OrdinalIgnoreCase)) candidate = $"www/{candidate}";
        var parts = candidate.Split('/');
        if (parts.Length != 2 || parts[1].Length == 0 || parts[1] is "." or ".." || parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("Staging-Pfad muss direkt unter www/ liegen.");
        return $"www/{parts[1]}";
    }
    private async Task<VirtualHostDefinition?> FindVirtualHostAsync(string? hostName, CancellationToken cancellationToken)
    {
        if (hostName is null) return null;
        foreach (var file in Directory.EnumerateFiles(paths.Resolve("app", "config", "vhosts"), "*.json"))
        {
            var host = await store.ReadAsync<VirtualHostDefinition>(file, cancellationToken).ConfigureAwait(false);
            if (host?.Host.Equals(hostName, StringComparison.OrdinalIgnoreCase) == true) return host;
        }
        return null;
    }
    private string ResolveDocumentRoot(string root) => Path.GetFullPath(Path.IsPathRooted(root) ? root : Path.Combine(paths.AppRoot, root.Replace('/', Path.DirectorySeparatorChar)));
    private static string GetHost(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host) ? uri.Host : throw new ArgumentException("Ungültige Staging-URL.");
    private static string VHostFileName(string host) { var invalid = Path.GetInvalidFileNameChars(); return new string(host.Trim().ToLowerInvariant().Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()) + ".json"; }
}
