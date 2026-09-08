using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using Drageon.Core;
using Drageon.Infrastructure;

namespace Drageon.App.ViewModels;

public sealed class PhpViewModel : ViewModelBase, IRefreshable
{
    private readonly AppServices _services;
    private readonly string? _requestedVersion;
    private string? _configuredVersion;
    private string _memoryLimit = "256M";
    private string _uploadMaxFilesize = "64M";
    private string _postMaxSize = "64M";
    private string _maxExecutionTime = "120";
    private bool _displayErrors = true;
    private bool _opcacheEnabled = true;
    private string _customIni = "";
    private string _status = "";

    public PhpViewModel(AppServices services, string? version = null)
    {
        _services = services;
        _requestedVersion = version;
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
        _ = RefreshAsync();
    }

    public string MemoryLimit { get => _memoryLimit; set => Set(ref _memoryLimit, value); }
    public string UploadMaxFilesize { get => _uploadMaxFilesize; set => Set(ref _uploadMaxFilesize, value); }
    public string PostMaxSize { get => _postMaxSize; set => Set(ref _postMaxSize, value); }
    public string MaxExecutionTime { get => _maxExecutionTime; set => Set(ref _maxExecutionTime, value); }
    public bool DisplayErrors { get => _displayErrors; set => Set(ref _displayErrors, value); }
    public bool OpcacheEnabled { get => _opcacheEnabled; set => Set(ref _opcacheEnabled, value); }
    public ObservableCollection<PhpExtensionOption> AvailableExtensions { get; } = [];
    public string CustomIni { get => _customIni; set => Set(ref _customIni, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string VersionLabel => _configuredVersion is null ? "" : $"· PHP {_configuredVersion}";
    public AsyncRelayCommand SaveCommand { get; }

    public async Task RefreshAsync()
    {
        try
        {
            _configuredVersion = _requestedVersion ?? await GetActiveVersionAsync();
            Raise(nameof(VersionLabel));
            if (_configuredVersion is null)
            {
                Status = T("php.status.activateFirst");
                AvailableExtensions.Clear();
                return;
            }

            var settings = await PhpVersionConfiguration.LoadAsync(_services.Paths, _services.Store, _configuredVersion);
            MemoryLimit = settings.MemoryLimit;
            UploadMaxFilesize = settings.UploadMaxFilesize;
            PostMaxSize = settings.PostMaxSize;
            MaxExecutionTime = settings.MaxExecutionTime.ToString(System.Globalization.CultureInfo.InvariantCulture);
            DisplayErrors = settings.DisplayErrors;
            OpcacheEnabled = settings.OpcacheEnabled;
            CustomIni = settings.CustomIni;
            await LoadExtensionsAsync(settings.Extensions, _configuredVersion);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private async Task SaveAsync()
    {
        if (!int.TryParse(MaxExecutionTime, out var executionTime) || executionTime < 0)
        {
            Status = T("php.error.executionTime");
            return;
        }

        var version = _configuredVersion ?? _requestedVersion ?? await GetActiveVersionAsync();
        if (version is null)
        {
            Status = T("php.status.activateFirst");
            return;
        }

        var settings = new PhpSettings
        {
            MemoryLimit = MemoryLimit,
            UploadMaxFilesize = UploadMaxFilesize,
            PostMaxSize = PostMaxSize,
            MaxExecutionTime = executionTime,
            DisplayErrors = DisplayErrors,
            OpcacheEnabled = OpcacheEnabled,
            Extensions = AvailableExtensions.Where(static item => item.IsEnabled && item.IsAvailable).Select(static item => item.IniValue).ToArray(),
            CustomIni = CustomIni
        };
        try
        {
            await _services.Store.WriteAsync(PhpVersionConfiguration.GetSettingsPath(_services.Paths, version), settings);
            var generator = _services.Generators.OfType<PhpConfigGenerator>().Single();
            await generator.GenerateForVersionAsync(version);
            Status = TF("php.status.savedVersion", version);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private async Task LoadExtensionsAsync(IReadOnlyList<string> enabledExtensions, string version)
    {
        AvailableExtensions.Clear();
        var phpRoot = _services.Paths.Resolve("bin", "php", version);
        var template = new[] { "php.ini-development", "php.ini-production", "php.ini" }
            .Select(name => Path.Combine(phpRoot, name)).FirstOrDefault(File.Exists);
        if (template is null)
        {
            Status = TF("php.status.templateMissing", version);
            return;
        }

        var content = await File.ReadAllTextAsync(template);
        var values = Regex.Matches(content, @"(?im)^\s*;?extension\s*=\s*(?<value>[^;\r\n]+)")
            .Select(match => match.Groups["value"].Value.Trim().Trim('"', '\''))
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(DisplayExtensionName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var loadable = await PhpExtensionProbe.GetLoadableAsync(phpRoot, values);

        foreach (var configured in enabledExtensions.Where(value => values.All(item => !item.Equals(value, StringComparison.OrdinalIgnoreCase))))
        {
            values.Add(configured);
        }

        foreach (var value in values)
        {
            var expectedDll = value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? value : $"php_{value}.dll";
            var available = File.Exists(Path.Combine(phpRoot, "ext", expectedDll))
                && (loadable is null || loadable.Contains(DisplayExtensionName(value)));
            AvailableExtensions.Add(new PhpExtensionOption(
                DisplayExtensionName(value), value, available,
                available && enabledExtensions.Any(item => DisplayExtensionName(item).Equals(DisplayExtensionName(value), StringComparison.OrdinalIgnoreCase))));
        }

        Status = TF("php.status.extensionsDetected", AvailableExtensions.Count, Path.GetFileName(template));
    }

    private async Task<string?> GetActiveVersionAsync()
    {
        var active = await _services.Store.ReadAsync<Dictionary<string, string>>(
            _services.Paths.Resolve("app", "config", "packages.active.json")) ?? [];
        return active.GetValueOrDefault("php");
    }

    private static string DisplayExtensionName(string value)
    {
        var name = Path.GetFileNameWithoutExtension(value);
        return name.StartsWith("php_", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
    }

}

public sealed class PhpExtensionOption(string name, string iniValue, bool isAvailable, bool isEnabled) : ViewModelBase
{
    private bool _isEnabled = isEnabled;
    public string Name { get; } = name;
    public string IniValue { get; } = iniValue;
    public bool IsAvailable { get; } = isAvailable;
    public string AvailabilityText => IsAvailable ? Localization.Get("php.extension.available") : Localization.Get("php.extension.missing");
    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
}
