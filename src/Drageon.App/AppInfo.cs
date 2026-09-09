namespace Drageon.App;

public static class AppInfo
{
    public const string AppName = "Drageon";
    public const string Copyright = "© 2026 Tim Sydekum · LIVID Media";
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "Unknown";
}
