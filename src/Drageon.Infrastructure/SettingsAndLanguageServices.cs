using System.Text.Json;
using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed class SettingsService(IPortablePathService paths, JsonFileStore store) : ISettingsService
{
    private readonly string _path = paths.Resolve("app", "config", "settings.json");

    public AppSettings Current { get; private set; } = new();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Current = await store.ReadAsync<AppSettings>(_path, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        await store.WriteAsync(_path, settings, cancellationToken).ConfigureAwait(false);
        Current = settings;
    }
}

public sealed class LanguageService(IPortablePathService paths) : ILanguageService
{
    private IReadOnlyDictionary<string, string> _translations = new Dictionary<string, string>();

    public string CurrentLanguage { get; private set; } = "de";

    public async Task LoadAsync(string language, CancellationToken cancellationToken = default)
    {
        var file = paths.Resolve("app", "languages", $"{language}.json");
        if (!File.Exists(file))
        {
            throw new FileNotFoundException($"Die Sprachdatei '{language}' wurde nicht gefunden.", file);
        }

        var translations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fallbackFile = paths.Resolve("app", "languages", "en.json");
        if (!file.Equals(fallbackFile, StringComparison.OrdinalIgnoreCase) && File.Exists(fallbackFile))
        {
            await using var fallbackStream = File.OpenRead(fallbackFile);
            foreach (var item in await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(fallbackStream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [])
                translations[item.Key] = item.Value;
        }
        await using var stream = File.OpenRead(file);
        foreach (var item in await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [])
            translations[item.Key] = item.Value;
        _translations = translations;
        CurrentLanguage = language;
    }

    public string Get(string key, string? fallback = null) =>
        _translations.TryGetValue(key, out var value) ? value : fallback ?? key;
}
