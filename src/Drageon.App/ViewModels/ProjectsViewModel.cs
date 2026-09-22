using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
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
        OpenProjectToolsCommand = new RelayCommand(OpenProjectTools, static parameter => parameter is ProjectItemViewModel);
        AssignDatabaseCommand = new AsyncRelayCommand(AssignDatabaseAsync, static parameter => parameter is ProjectItemViewModel);
        CreateSnapshotCommand = new AsyncRelayCommand(CreateSnapshotAsync, static parameter => parameter is ProjectItemViewModel);
        ManageSnapshotCommand = new AsyncRelayCommand(ManageSnapshotAsync, static parameter => parameter is ProjectItemViewModel { HasSnapshots: true });
        CreateStagingCommand = new AsyncRelayCommand(CreateStagingAsync, static parameter => parameter is ProjectItemViewModel { IsStaging: false });
        _ = RefreshAsync();
    }

    public ObservableCollection<ProjectItemViewModel> Projects { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ToggleFavoriteCommand { get; }
    public RelayCommand OpenProjectCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand OpenPowerShellCommand { get; }
    public RelayCommand OpenProjectToolsCommand { get; }
    public AsyncRelayCommand AssignDatabaseCommand { get; }
    public AsyncRelayCommand CreateSnapshotCommand { get; }
    public AsyncRelayCommand ManageSnapshotCommand { get; }
    public AsyncRelayCommand CreateStagingCommand { get; }
    public Visibility EmptyVisibility => Projects.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Status { get => _status; private set => Set(ref _status, value); }

    public async Task RefreshAsync()
    {
        try
        {
            Status = T("projects.status.loading");
            var projects = await _services.Projects.GetProjectsAsync();
            HashSet<string>? availableDatabases = null;
            try
            {
                availableDatabases = (await _services.DatabaseBackups.GetDatabasesAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception exception)
            {
                _services.Logs.Write("Database", exception.Message, LogSeverity.Warning);
            }
            var snapshotsByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var project in projects)
            {
                var snapshots = await _services.ProjectSnapshots.GetProjectSnapshotsAsync(project.RelativePath);
                snapshotsByPath[project.RelativePath] = snapshots.Count;
            }
            var projectPaths = projects.Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stagingCounts = projects.Where(item => !string.IsNullOrWhiteSpace(item.StagingOf))
                .GroupBy(item => item.StagingOf!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            var children = projects.Where(item => !string.IsNullOrWhiteSpace(item.StagingOf) && projectPaths.Contains(item.StagingOf!))
                .GroupBy(item => item.StagingOf!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
            var ordered = new List<(LocalProject Project, int Depth)>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddProject(LocalProject project, int depth)
            {
                if (!visited.Add(project.RelativePath)) return;
                ordered.Add((project, depth));
                if (children.TryGetValue(project.RelativePath, out var stagingProjects)) foreach (var stagingProject in stagingProjects) AddProject(stagingProject, depth + 1);
            }
            foreach (var project in projects.Where(item => string.IsNullOrWhiteSpace(item.StagingOf) || !projectPaths.Contains(item.StagingOf!)).OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)) AddProject(project, 0);
            foreach (var project in projects) AddProject(project, 0);
            Projects.Clear();
            foreach (var item in ordered)
            {
                var databaseMissing = item.Project.DatabaseName is not null && availableDatabases is not null && !availableDatabases.Contains(item.Project.DatabaseName);
                Projects.Add(new ProjectItemViewModel(item.Project, snapshotsByPath.GetValueOrDefault(item.Project.RelativePath), stagingCounts.GetValueOrDefault(item.Project.RelativePath), databaseMissing, item.Depth));
            }
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

    private void OpenProjectTools(object? parameter)
    {
        var project = (ProjectItemViewModel)parameter!;
        new ProjectToolsWindow
        {
            DataContext = new ProjectToolsDialogViewModel(this, project),
            Owner = Application.Current.MainWindow
        }.ShowDialog();
        _ = RefreshAsync();
    }

    private async Task AssignDatabaseAsync(object? parameter)
    {
        var project = (ProjectItemViewModel)parameter!;
        try
        {
            var connection = await EnsureDatabaseConnectionAsync(); if (connection is null) return;
            var databases = await _services.DatabaseBackups.GetDatabasesAsync(connection);
            var suggestion = project.DatabaseName ?? databases.FirstOrDefault(name => name.Equals(project.Id, StringComparison.OrdinalIgnoreCase));
            var choices = new List<(string? Value, string Label)> { ("", T("projects.database.none")) };
            choices.AddRange(databases.Select(name => ((string?)name, name)));
            var selected = ChoiceDialog.Show(T("projects.database.assign"), T("projects.database.select"), choices, suggestion);
            if (selected is null) return;
            await _services.Projects.SetDatabaseAsync(project.RelativePath, string.IsNullOrWhiteSpace(selected) ? null : selected);
            await RefreshAsync();
        }
        catch (Exception exception) { Fail(exception); }
    }

    private async Task CreateSnapshotAsync(object? parameter)
    {
        var project = (ProjectItemViewModel)parameter!;
        if (project.IsDatabaseMissing) { Status = TF("projects.database.missingAction", project.DatabaseName); return; }
        var description = ChoiceDialog.ShowText(T("snapshots.create"), T("snapshots.descriptionPrompt"), "");
        if (description is null) return;
        try
        {
            Status = T("snapshots.status.creating");
            var connection = project.DatabaseName is null ? null : await EnsureDatabaseConnectionAsync();
            if (project.DatabaseName is not null && connection is null) return;
            await _services.ProjectSnapshots.CreateProjectSnapshotAsync(project.RelativePath, description, connection, new Progress<string>(step => Status = T($"snapshots.step.{step}")));
            await RefreshAsync(); Status = T("snapshots.status.created");
        }
        catch (Exception exception) { Fail(exception); }
    }

    private async Task ManageSnapshotAsync(object? parameter)
    {
        var project = (ProjectItemViewModel)parameter!;
        try
        {
            var snapshots = await _services.ProjectSnapshots.GetProjectSnapshotsAsync(project.RelativePath);
            var choice = ChoiceDialog.ShowSnapshotManage(T("snapshots.manage"), snapshots.Select(item => (item.DirectoryPath,
                TF("snapshots.choice", item.CreatedAt.ToString("g"), item.Description, item.DatabaseName ?? T("projects.database.none"), item.IncludesVirtualHost ? T("common.yes") : T("common.no")))));
            if (choice is null || choice.Value.Value is null) return;
            if (choice.Value.Action == SnapshotManageAction.Delete)
            {
                if (MessageBox.Show(T("snapshots.confirm.delete"), T("action.delete"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                await _services.ProjectSnapshots.DeleteProjectSnapshotAsync(choice.Value.Value);
                await RefreshAsync(); Status = T("snapshots.status.deleted");
                return;
            }
            if (MessageBox.Show(TF("snapshots.confirm.restoreProject", project.Name), T("snapshots.restore"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var selectedSnapshot = snapshots.First(item => item.DirectoryPath.Equals(choice.Value.Value, StringComparison.OrdinalIgnoreCase));
            var connection = selectedSnapshot.DatabaseName is null ? null : await EnsureDatabaseConnectionAsync();
            if (selectedSnapshot.DatabaseName is not null && connection is null) return;
            await _services.ProjectSnapshots.RestoreProjectSnapshotAsync(choice.Value.Value, choice.Value.BackupCurrent, connection, new Progress<string>(step => Status = T($"snapshots.step.{step}")));
            await ReloadWebServerAsync();
            Status = T("snapshots.status.restored");
            await RefreshAsync();
        }
        catch (Exception exception) { Fail(exception); }
    }

    private async Task CreateStagingAsync(object? parameter)
    {
        var project = (ProjectItemViewModel)parameter!;
        if (project.IsStaging) return;
        if (project.IsDatabaseMissing) { Status = TF("projects.database.missingAction", project.DatabaseName); return; }
        var stagingNumber = GetNextStagingNumber(project);
        var displaySuffix = stagingNumber == 1 ? "" : $" {stagingNumber}";
        var pathSuffix = stagingNumber == 1 ? "" : $"-{stagingNumber}";
        var databaseSuffix = stagingNumber == 1 ? "" : $"_{stagingNumber}";
        var name = ChoiceDialog.ShowText(T("staging.create"), T("staging.projectName"), $"{project.Name} – Staging{displaySuffix}"); if (string.IsNullOrWhiteSpace(name)) return;
        var path = ChoiceDialog.ShowText(T("staging.create"), T("staging.path"), $"www/{project.Id}-staging{pathSuffix}"); if (string.IsNullOrWhiteSpace(path)) return;
        var url = ChoiceDialog.ShowText(T("staging.create"), T("staging.url"), $"http://{project.Id}-staging{pathSuffix}.localhost"); if (string.IsNullOrWhiteSpace(url)) return;
        var copy = project.DatabaseName is not null && MessageBox.Show(TF("staging.copyDatabase", project.DatabaseName), T("staging.create"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        string? database = null;
        if (copy) { database = ChoiceDialog.ShowText(T("staging.create"), T("staging.database"), $"{project.DatabaseName}_staging{databaseSuffix}"); if (string.IsNullOrWhiteSpace(database)) return; }
        var createVhost = MessageBox.Show(T("staging.createVhost"), T("staging.create"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        try
        {
            var connection = copy ? await EnsureDatabaseConnectionAsync() : null; if (copy && connection is null) return;
            var result = await _services.Staging.CreateAsync(new(project.RelativePath, name, path, url, database, copy, createVhost), connection, new Progress<string>(step => Status = T($"staging.step.{step}")));
            await ReloadWebServerAsync();
            var message = result.DatabaseName is null
                ? TF("staging.successWithoutDatabase", result.Url)
                : TF("staging.success", result.DatabaseName, result.Url);
            MessageBox.Show(message, T("staging.create"), MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAsync();
        }
        catch (Exception exception) { Fail(exception); }
    }

    private int GetNextStagingNumber(ProjectItemViewModel project)
    {
        var number = Math.Max(1, Projects.Count(item => item.StagingOf?.Equals(project.RelativePath, StringComparison.OrdinalIgnoreCase) == true) + 1);
        while (true)
        {
            var suffix = number == 1 ? "" : $"-{number}";
            var relativePath = $"www/{project.Id}-staging{suffix}";
            if (!Projects.Any(item => item.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase))
                && !Directory.Exists(_services.Paths.Resolve(relativePath.Split('/')))) return number;
            number++;
        }
    }

    internal async Task SaveProjectNameAsync(ProjectItemViewModel project, string projectName)
    {
        var name = projectName.Trim();
        if (name.Length == 0) throw new InvalidDataException(T("projects.name.required"));
        await _services.Projects.SetNameAsync(project.RelativePath,
            name.Equals(project.Id, StringComparison.CurrentCulture) ? null : name);
        project.UpdateName(name);
        Status = TF("projects.name.saved", name);
    }

    internal void ReportProjectToolFailure(Exception exception) => Fail(exception);

    internal async Task<bool> DeleteStagingAsync(ProjectItemViewModel project)
    {
        if (MessageBox.Show(TF("staging.confirm.delete", project.Name, project.DatabaseName ?? T("projects.database.none")), T("staging.delete"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        try
        {
            var connection = project.DatabaseName is null ? null : await EnsureDatabaseConnectionAsync(); if (project.DatabaseName is not null && connection is null) return false;
            await _services.Staging.DeleteAsync(project.RelativePath, project.DatabaseName is not null, connection); await ReloadWebServerAsync(); await RefreshAsync();
            return true;
        }
        catch (Exception exception) { Fail(exception); return false; }
    }

    private void Fail(Exception exception) { Status = exception.Message; _services.Logs.Write("Projects", exception.Message, LogSeverity.Error); }

    private async Task<DatabaseConnection?> EnsureDatabaseConnectionAsync()
    {
        try
        {
            var active = await _services.DatabaseBackups.GetActiveServerAsync();
            _ = await _services.DatabaseBackups.GetDatabasesAsync(active.Connection);
            return active.Connection;
        }
        catch (Exception exception)
        {
            Status = T("backups.status.configureCredentials");
            _services.Logs.Write("Database", exception.Message, LogSeverity.Warning);
            return null;
        }
    }

    private async Task ReloadWebServerAsync()
    {
        var active = _services.Settings.Current.WebServer;
        var generator = _services.Generators.FirstOrDefault(item => item.ComponentId.Equals(active, StringComparison.OrdinalIgnoreCase));
        if (generator is not null && await Drageon.Infrastructure.VersionedComponentConfiguration.GetActiveVersionAsync(_services.Paths, _services.Store, active) is not null)
            await generator.GenerateAsync();
        if (_services.Processes.GetStatuses().Any(item => item.Id.Equals(active, StringComparison.OrdinalIgnoreCase) && item.State == ComponentState.Running))
        {
            await _services.Runtime.StopAsync(active);
            await _services.Runtime.StartAsync(active);
        }
    }
}

public sealed class ProjectItemViewModel(LocalProject project, int snapshotCount, int stagingCount, bool isDatabaseMissing, int hierarchyDepth) : ViewModelBase
{
    private bool _isFavorite = project.IsFavorite;
    private string _name = project.Name;

    public string Id { get; } = project.Id;
    public string Name => _name;
    public string DirectoryPath { get; } = project.DirectoryPath;
    public string RelativePath { get; } = project.RelativePath;
    public string Url { get; } = project.Url;
    public string Type { get; } = project.Type;
    public string Technologies => string.Join(", ", project.Technologies);
    public bool HasVirtualHost { get; } = project.HasVirtualHost;
    public string? DatabaseName { get; } = project.DatabaseName;
    public string? StagingOf { get; } = project.StagingOf;
    public bool HasDatabase => DatabaseName is not null;
    public int SnapshotCount { get; } = snapshotCount;
    public int StagingCount { get; } = stagingCount;
    public bool HasSnapshots => SnapshotCount > 0;
    public bool IsDatabaseMissing { get; } = isDatabaseMissing;
    public string DatabaseLabel => IsDatabaseMissing
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("projects.database.missing"), DatabaseName)
        : DatabaseName ?? "";
    public string DatabaseToolTip => IsDatabaseMissing ? Localization.Get("projects.database.missingHint") : DatabaseName ?? "";
    public Visibility DatabaseVisibility => HasDatabase ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SnapshotActionsVisibility => HasSnapshots ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MainProjectVisibility => IsStaging ? Visibility.Collapsed : Visibility.Visible;
    public Thickness ProjectIndent { get; } = new(hierarchyDepth * 12, 0, 0, 0);
    public string HierarchyPrefix => hierarchyDepth > 0 ? "↳ " : "";
    public string SnapshotStagingSummary => IsStaging
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("projects.snapshotCount"), SnapshotCount)
        : string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("projects.snapshotStagingCount"), SnapshotCount, StagingCount);
    public string SnapshotHeading => string.Format(System.Globalization.CultureInfo.CurrentCulture, Localization.Get("projects.tools.snapshotHeading"), SnapshotCount);
    public string StagingHeading => Localization.Get("projects.tools.staging");
    public string StagingHint => IsStaging
        ? Localization.Get("projects.tools.stagingDeleteHint")
        : Localization.Get("projects.tools.stagingHint");
    public bool IsStaging => !string.IsNullOrWhiteSpace(StagingOf);
    public Visibility StagingDeleteVisibility => IsStaging ? Visibility.Visible : Visibility.Collapsed;
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

    internal void UpdateName(string name) => Set(ref _name, name, nameof(Name));
}

public sealed class ProjectToolsDialogViewModel : ViewModelBase
{
    private string _projectName;
    private string _nameStatus = "";

    public ProjectToolsDialogViewModel(ProjectsViewModel owner, ProjectItemViewModel project)
    {
        Owner = owner;
        Project = project;
        _projectName = project.Name;
        SaveProjectNameCommand = new AsyncRelayCommand(_ => SaveProjectNameAsync(), _ => CanSaveName);
        DeleteStagingCommand = new AsyncRelayCommand(_ => DeleteStagingAsync(), _ => Project.IsStaging);
    }

    public ProjectsViewModel Owner { get; }
    public ProjectItemViewModel Project { get; }
    public AsyncRelayCommand SaveProjectNameCommand { get; }
    public AsyncRelayCommand DeleteStagingCommand { get; }
    public event EventHandler? CloseRequested;
    public string ProjectName
    {
        get => _projectName;
        set
        {
            if (!Set(ref _projectName, value)) return;
            NameStatus = "";
            SaveProjectNameCommand.RaiseCanExecuteChanged();
        }
    }
    public string NameStatus { get => _nameStatus; private set => Set(ref _nameStatus, value); }
    private bool CanSaveName => !string.IsNullOrWhiteSpace(ProjectName)
        && !ProjectName.Trim().Equals(Project.Name, StringComparison.CurrentCulture);

    private async Task SaveProjectNameAsync()
    {
        try
        {
            await Owner.SaveProjectNameAsync(Project, ProjectName);
            ProjectName = Project.Name;
            NameStatus = TF("projects.name.saved", Project.Name);
            SaveProjectNameCommand.RaiseCanExecuteChanged();
        }
        catch (Exception exception)
        {
            NameStatus = exception.Message;
            Owner.ReportProjectToolFailure(exception);
        }
    }

    private async Task DeleteStagingAsync()
    {
        if (await Owner.DeleteStagingAsync(Project)) CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
