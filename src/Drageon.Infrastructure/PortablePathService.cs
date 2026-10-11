using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed class PortablePathService : IPortablePathService
{
    private static readonly string[][] RequiredDirectories =
    [
        ["www"], ["data", "mariadb"], ["data", "mysql"], ["data", "mail"], ["data", "backups", "databases"], ["data", "snapshots"],
        ["bin", "apache"], ["bin", "nginx"], ["app", "config", "vhosts"], ["app", "var", "logs", "nginx"], ["bin", "php"], ["bin", "mariadb"], ["bin", "mysql"], ["bin", "phpmyadmin"], ["bin", "curl"],
        ["app", "config", "php"], ["app", "config", "mariadb"], ["app", "config", "mysql"], ["app", "config", "phpmyadmin"],
        ["app", "languages"], ["app", "packages"],
        ["app", "var", "generated", "apache"], ["app", "var", "generated", "php"], ["app", "var", "generated", "mariadb"], ["app", "var", "generated", "mysql"], ["app", "var", "generated", "phpmyadmin"],
        ["app", "var", "logs", "app"], ["app", "var", "logs", "apache"], ["app", "var", "logs", "php"],
        ["app", "var", "logs", "mariadb"], ["app", "var", "logs", "mysql"], ["app", "var", "logs", "cron"],
        ["app", "var", "tmp", ".opcache"], ["app", "var", "tmp", "downloads"], ["app", "var", "tmp", "extract"], ["app", "var", "tmp", "phpmyadmin"]
    ];

    public PortablePathService(string? appRoot = null)
    {
        AppRoot = Path.GetFullPath(appRoot ?? AppContext.BaseDirectory);
    }

    public string AppRoot { get; }

    public string Resolve(params string[] segments)
    {
        var path = segments.Aggregate(AppRoot, Path.Combine);
        var resolved = Path.GetFullPath(path);
        if (!OperationSafety.Contains(AppRoot, resolved))
        {
            throw new InvalidOperationException("Der Pfad liegt außerhalb des portablen App-Verzeichnisses.");
        }

        return resolved;
    }

    public void EnsureDirectoryLayout()
    {
        foreach (var directory in RequiredDirectories)
        {
            Directory.CreateDirectory(Resolve(directory));
        }
    }
}
