using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using Drageon.Core;
using Drageon.Infrastructure;

namespace Drageon.App.ViewModels;

public sealed class PackageManifestEditorViewModel : ViewModelBase
{
    private static readonly Regex SafeIdPattern = new("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern = new("^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant);
    private readonly AppServices _services;
    private readonly bool _isNew;
    private string _id;
    private string _displayName;
    private string _description;
    private string _category;
    private readonly bool _bundled;
    private string _exclusiveGroup;
    private string _downloadPageUrl;
    private string _installDirectory;
    private bool _addToPath;
    private string _pathDirectories;
    private string _status = "";

    public PackageManifestEditorViewModel(AppServices services, PackageManifest? manifest = null)
    {
        _services = services;
        _isNew = manifest is null;
        _id = manifest?.Id ?? "";
        _displayName = manifest?.DisplayName ?? "";
        _description = manifest?.Description ?? "";
        _category = string.IsNullOrWhiteSpace(manifest?.Category) ? "other" : manifest.Category;
        _bundled = manifest?.Bundled ?? false;
        _exclusiveGroup = manifest?.ExclusiveGroup ?? "";
        _downloadPageUrl = manifest?.DownloadPageUrl ?? "";
        _installDirectory = manifest?.InstallDirectory ?? "";
        _addToPath = manifest?.AddToPath ?? false;
        _pathDirectories = string.Join("; ", manifest?.PathDirectories ?? []);

        foreach (var release in manifest?.Releases ?? [])
        {
            Releases.Add(new PackageReleaseEditorViewModel(release));
        }
        if (Releases.Count == 0) Releases.Add(new PackageReleaseEditorViewModel());

        AddReleaseCommand = new RelayCommand(_ => Releases.Insert(0, new PackageReleaseEditorViewModel()));
        RemoveReleaseCommand = new RelayCommand(RemoveRelease, static parameter => parameter is PackageReleaseEditorViewModel);
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
    }

    public event EventHandler? Saved;

    public string Title => T(_isNew ? "packageEditor.title.new" : "packageEditor.title.edit");
    public bool IsIdReadOnly => !_isNew;
    public string Id { get => _id; set => Set(ref _id, value); }
    public string DisplayName { get => _displayName; set => Set(ref _displayName, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Category { get => _category; set => Set(ref _category, value); }
    public string ExclusiveGroup { get => _exclusiveGroup; set => Set(ref _exclusiveGroup, value); }
    public IReadOnlyList<PackageCategoryOption> Categories { get; } =
    [
        new("webserver", Localization.Get("packages.category.webserver")),
        new("php", Localization.Get("packages.category.php")),
        new("database", Localization.Get("packages.category.database")),
        new("developmentTool", Localization.Get("packages.category.developmentTool")),
        new("other", Localization.Get("packages.category.other"))
    ];
    public string DownloadPageUrl { get => _downloadPageUrl; set => Set(ref _downloadPageUrl, value); }
    public string InstallDirectory { get => _installDirectory; set => Set(ref _installDirectory, value); }
    public bool AddToPath { get => _addToPath; set => Set(ref _addToPath, value); }
    public string PathDirectories { get => _pathDirectories; set => Set(ref _pathDirectories, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public ObservableCollection<PackageReleaseEditorViewModel> Releases { get; } = [];
    public RelayCommand AddReleaseCommand { get; }
    public RelayCommand RemoveReleaseCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }

    private void RemoveRelease(object? parameter)
    {
        if (parameter is PackageReleaseEditorViewModel release) Releases.Remove(release);
    }

    private async Task SaveAsync()
    {
        var id = Id.Trim();
        var displayName = DisplayName.Trim();
        var installDirectory = string.IsNullOrWhiteSpace(InstallDirectory) ? id : InstallDirectory.Trim();
        var pathDirectories = PathDirectories.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (pathDirectories.Any(directory => !PortableEnvironment.IsValidPathDirectory(directory)))
        {
            Status = T("packageEditor.error.pathDirectories");
            return;
        }

        if (!SafeIdPattern.IsMatch(id))
        {
            Status = T("packageEditor.error.id");
            return;
        }
        if (string.IsNullOrWhiteSpace(displayName) || !SafeIdPattern.IsMatch(installDirectory))
        {
            Status = T("packageEditor.error.general");
            return;
        }
        if (!string.IsNullOrWhiteSpace(DownloadPageUrl)
            && (!Uri.TryCreate(DownloadPageUrl.Trim(), UriKind.Absolute, out var downloadPageUri)
                || downloadPageUri.Scheme is not ("http" or "https")))
        {
            Status = T("packageEditor.error.downloadPageUrl");
            return;
        }
        if (Releases.Count == 0)
        {
            Status = T("packageEditor.error.releaseRequired");
            return;
        }
        if (Releases.GroupBy(static release => release.Version.Trim(), StringComparer.OrdinalIgnoreCase).Any(static group => group.Key.Length == 0 || group.Count() > 1))
        {
            Status = T("packageEditor.error.versions");
            return;
        }

        foreach (var release in Releases)
        {
            if (!Uri.TryCreate(release.Url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                Status = TF("packageEditor.error.url", release.Version);
                return;
            }
            if (!string.IsNullOrWhiteSpace(release.Sha256) && !Sha256Pattern.IsMatch(release.Sha256.Trim()))
            {
                Status = TF("packageEditor.error.sha256", release.Version);
                return;
            }
            if (!release.IsArchive && !release.IsSingleFile)
            {
                Status = TF("packageEditor.error.archiveType", release.Version);
                return;
            }
            if (release.IsSingleFile)
            {
                var fileName = string.IsNullOrWhiteSpace(release.FileName) ? Path.GetFileName(uri.LocalPath) : release.FileName.Trim();
                if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or ".."
                    || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || fileName.Contains('/') || fileName.Contains('\\'))
                {
                    Status = TF("packageEditor.error.fileName", release.Version);
                    return;
                }
            }
        }

        var file = _services.Paths.Resolve("app", "packages", id + ".json");
        if (_isNew && File.Exists(file))
        {
            Status = T("packageEditor.error.exists");
            return;
        }

        var manifest = new PackageManifest
        {
            Id = id,
            DisplayName = displayName,
            Description = Description.Trim(),
            Category = Category.Trim(),
            Bundled = _bundled,
            ExclusiveGroup = string.IsNullOrWhiteSpace(ExclusiveGroup) ? null : ExclusiveGroup.Trim(),
            DownloadPageUrl = string.IsNullOrWhiteSpace(DownloadPageUrl) ? null : DownloadPageUrl.Trim(),
            InstallDirectory = installDirectory,
            AddToPath = AddToPath,
            PathDirectories = pathDirectories,
            Releases = Releases.Select(static release => release.ToModel()).ToArray()
        };
        await _services.Store.WriteAsync(file, manifest);
        await _services.Packages.RefreshEnvironmentAsync();
        Status = T("packageEditor.status.saved");
        Saved?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class PackageReleaseEditorViewModel : ViewModelBase
{
    private string _version;
    private string _url;
    private string _sha256;
    private string _archiveType;
    private string _fileName;
    private string _stripPrefix;
    private string _launchPath;

    public PackageReleaseEditorViewModel(PackageRelease? release = null)
    {
        _version = release?.Version ?? "";
        _url = release?.Url ?? "";
        _sha256 = release?.Sha256 ?? "";
        _archiveType = release?.ArchiveType.Trim().ToLowerInvariant() ?? "zip";
        _fileName = release?.FileName ?? "";
        _stripPrefix = release?.StripPrefix ?? "";
        _launchPath = release?.LaunchPath ?? "";
    }

    public string Version { get => _version; set => Set(ref _version, value); }
    public string Url { get => _url; set => Set(ref _url, value); }
    public string Sha256 { get => _sha256; set => Set(ref _sha256, value); }
    public IReadOnlyList<PackageDownloadTypeOption> DownloadTypes { get; } =
    [
        new("zip", Localization.Get("packageEditor.downloadType.zip")),
        new("file", Localization.Get("packageEditor.downloadType.file"))
    ];
    public string ArchiveType
    {
        get => _archiveType;
        set
        {
            if (!Set(ref _archiveType, value)) return;
            Raise(nameof(IsArchive));
            Raise(nameof(IsSingleFile));
        }
    }
    public bool IsArchive => string.Equals(ArchiveType, "zip", StringComparison.OrdinalIgnoreCase);
    public bool IsSingleFile => string.Equals(ArchiveType, "file", StringComparison.OrdinalIgnoreCase);
    public string FileName { get => _fileName; set => Set(ref _fileName, value); }
    public string StripPrefix { get => _stripPrefix; set => Set(ref _stripPrefix, value); }
    public string LaunchPath { get => _launchPath; set => Set(ref _launchPath, value); }

    public PackageRelease ToModel() => new()
    {
        Version = Version.Trim(),
        Url = Url.Trim(),
        Sha256 = string.IsNullOrWhiteSpace(Sha256) ? null : Sha256.Trim(),
        ArchiveType = ArchiveType.Trim(),
        FileName = IsSingleFile && !string.IsNullOrWhiteSpace(FileName) ? FileName.Trim() : null,
        StripPrefix = IsArchive && !string.IsNullOrWhiteSpace(StripPrefix) ? StripPrefix.Trim() : null,
        LaunchPath = string.IsNullOrWhiteSpace(LaunchPath) ? null : LaunchPath.Trim()
    };
}

public sealed record PackageDownloadTypeOption(string Value, string DisplayName);
public sealed record PackageCategoryOption(string Value, string DisplayName);
