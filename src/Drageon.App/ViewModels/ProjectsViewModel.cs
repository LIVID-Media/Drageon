using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Drageon.Core;

namespace Drageon.App.ViewModels;

public sealed class ProjectsViewModel : ViewModelBase, IRefreshable
{
    private readonly AppServices _services;
    private string _status;

    public ProjectsViewModel(AppServices services)
    {
        _services = services;
        _status = T("projects.status.loading");
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        ToggleFavoriteCommand = new AsyncRelayCommand(ToggleFavoriteAsync, static parameter => parameter is ProjectItemViewModel);
        OpenProjectCommand = new RelayCommand(OpenProject, static parameter => parameter is ProjectItemViewModel);
        OpenFolderCommand = new RelayCommand(OpenFolder, static parameter => parameter is ProjectItemViewModel);
        OpenPowerShellCommand = new AsyncRelayCommand(OpenPowerShellAsync, static parameter => parameter is ProjectItemViewModel);
        _ = RefreshAsync();
    }

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand OpenPowerShellCommand { get; }
    public Visibility EmptyVisibility => Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Status { get => _status; private set => Set(ref _status, value); }

    public async Task RefreshAsync()
    {
        try
        {
            Status = T("projects.status.loading");
            var projects = await _services.Projects.GetProjectsAsync();
            Projects.Clear();
            foreach (var project in projects) Projects.Add(new ProjectItemViewModel(project));
            Raise(nameof(EmptyVisibility));
            Status = TF("projects.status.found", Projects.Count);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            _services.Logs.Write("Projects", exception.Message, LogSeverity.Error);
        }
    }

    private async Task ToggleFavoriteAsync(object? parameter)
    {
        var project = (ProjectItemViewModel)parameter!;
        var isFavorite = !project.IsFavorite;
        try
        {
            await _services.Projects.SetFavoriteAsync(project.Id, isFavorite);
            project.IsFavorite = isFavorite;
            Status = isFavorite
                ? TF("projects.status.favoriteAdded", project.Name)
                : TF("projects.status.favoriteRemoved", project.Name);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
        }
    }

    private static void OpenProject(object? parameter)
    {
        var project = (ProjectItemViewModel)parameter!;
        _ = Process.Start(new ProcessStartInfo(project.Url) { UseShellExecute = true });
    }

    private static void OpenFolder(object? parameter)
    {
        var project = (ProjectItemViewModel)parameter!;
        _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{project.DirectoryPath}\"") { UseShellExecute = true });
    }

    private async Task OpenPowerShellAsync(object? parameter)
    {
        var project = (ProjectItemViewModel)parameter!;
        try
        {
            Status = await PowerShellLauncher.OpenAsync(_services, project.DirectoryPath);
        }
        catch (Exception exception)
        {
            Status = exception.Message;
            _services.Logs.Write("Projects", exception.Message, LogSeverity.Error);
        }
    }
}

public sealed class ProjectItemViewModel(LocalProject project) : ViewModelBase
{
    private bool _isFavorite = project.IsFavorite;

    public string Id { get; } = project.Id;
    public string Name { get; } = project.Name;
    public string DirectoryPath { get; } = project.DirectoryPath;
    public string Url { get; } = project.Url;
    public string Type { get; } = project.Type;
    public string Technologies => string.Join(", ", project.Technologies);
    public bool HasVirtualHost { get; } = project.HasVirtualHost;
    public string UrlToolTip => HasVirtualHost ? Localization.Get("projects.url.vhost") : Localization.Get("projects.url.folder");
    public string FavoriteGlyph => IsFavorite ? "\uE735" : "\uE734";
    public string FavoriteToolTip => IsFavorite ? Localization.Get("projects.favorite.remove") : Localization.Get("projects.favorite.add");

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (!Set(ref _isFavorite, value)) return;
            Raise(nameof(FavoriteGlyph));
            Raise(nameof(FavoriteToolTip));
        }
    }
}
