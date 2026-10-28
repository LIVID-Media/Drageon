using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Drageon.Core;

namespace Drageon.App.ViewModels;

public sealed class BackupsViewModel : ViewModelBase, IRefreshable
{
    private readonly AppServices _services;
    private string _status = "";
    private DatabaseBackupListItem? _selectedBackup;
    private string? _targetDatabase;

    public BackupsViewModel(AppServices services)
    {
        _services = services;
        RefreshCommand = new(_ => RefreshAsync());
        BackupSelectedCommand = new(_ => BackupAsync(Databases.Where(item => item.IsSelected).Select(item => item.Name)), _ => Databases.Any(item => item.IsSelected));
        BackupAllCommand = new(_ => BackupAsync(Databases.Select(item => item.Name)));
        RestoreCommand = new(_ => RestoreAsync(), _ => SelectedBackup is not null && !string.IsNullOrWhiteSpace(TargetDatabase));
        DeleteBackupCommand = new(_ => DeleteBackupAsync(), _ => SelectedBackup is not null);
        OpenBackupFolderCommand = new RelayCommand(_ => OpenBackupFolder());
        _ = RefreshAsync();
    }

    public ObservableCollection<SelectableDatabase> Databases { get; } = [];
    public ObservableCollection<DatabaseBackupListItem> Backups { get; } = [];
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand BackupSelectedCommand { get; }
    public AsyncRelayCommand BackupAllCommand { get; }
    public AsyncRelayCommand RestoreCommand { get; }
    public AsyncRelayCommand DeleteBackupCommand { get; }
    public RelayCommand OpenBackupFolderCommand { get; }
    public DatabaseBackupListItem? SelectedBackup { get => _selectedBackup; set { if (!Set(ref _selectedBackup, value)) return; if (value is not null) TargetDatabase = value.DatabaseName; RestoreCommand.RaiseCanExecuteChanged(); DeleteBackupCommand.RaiseCanExecuteChanged(); } }
    public string? TargetDatabase { get => _targetDatabase; set { if (Set(ref _targetDatabase, value)) RestoreCommand.RaiseCanExecuteChanged(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public Visibility BackupActionsVisibility => Backups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public async Task RefreshAsync()
    {
        try
        {
            Status = T("backups.status.loadingLocal");
            Backups.Clear();
            foreach (var backup in await _services.DatabaseBackups.GetBackupsAsync()) Backups.Add(new(backup, FormatDate(backup.CreatedAt)));
            SelectedBackup = null;
            Raise(nameof(BackupActionsVisibility));
            await LoadDatabasesAsync();
        }
        catch (Exception exception)
        {
            Databases.Clear(); Status = T("backups.status.configureCredentials");
            _services.Logs.Write("Database", exception.Message, LogSeverity.Warning);
        }
    }

    private async Task LoadDatabasesAsync()
    {
        var names = await _services.DatabaseBackups.GetDatabasesAsync();
        Databases.Clear(); foreach (var name in names) Databases.Add(new(name, () => BackupSelectedCommand.RaiseCanExecuteChanged()));
        BackupSelectedCommand.RaiseCanExecuteChanged();
        TargetDatabase = names.Contains(TargetDatabase, StringComparer.OrdinalIgnoreCase) ? TargetDatabase : names.FirstOrDefault();
        Status = TF("backups.status.ready", names.Count);
    }

    private async Task BackupAsync(IEnumerable<string> names)
    {
        try
        {
            var selected = names.ToArray(); if (selected.Length == 0) return;
            Status = T("backups.status.running");
            await _services.DatabaseBackups.BackupAsync(selected, progress: new Progress<string>(name => Status = TF("backups.status.database", name)));
            await RefreshAsync();
        }
        catch (Exception exception) { Fail(exception); }
    }

    private async Task RestoreAsync()
    {
        if (SelectedBackup is null || string.IsNullOrWhiteSpace(TargetDatabase)) return;
        try
        {
            var backup = SelectedBackup.Path;
            var target = TargetDatabase;
            var plan = await _services.DatabaseBackups.GetRestorePlanAsync(backup, target);
            if (!ChoiceDialog.ConfirmOperationPlan(T("backups.restore"), plan)) return;
            Status = T("backups.status.restoring");
            await _services.DatabaseBackups.RestoreAsync(backup, target, confirmedPlan: plan.Fingerprint);
            await RefreshAsync();
            Status = T("backups.status.restored");
        }        catch (Exception exception) { Fail(exception); }
    }

    private async Task DeleteBackupAsync()
    {
        if (SelectedBackup is null || MessageBox.Show(TF("backups.confirm.delete", SelectedBackup.FileName), T("action.delete"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { await _services.DatabaseBackups.DeleteBackupAsync(SelectedBackup.Path); await RefreshAsync(); Status = T("backups.status.deleted"); }
        catch (Exception exception) { Fail(exception); }
    }

    private void OpenBackupFolder()
    {
        try
        {
            var directory = _services.Paths.Resolve("data", "backups", "databases");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
            Status = T("backups.status.folderOpened");
        }
        catch (Exception exception) { Fail(exception); }
    }

    private string FormatDate(DateTimeOffset value)
    {
        var culture = _services.Language.CurrentLanguage.ToLowerInvariant() switch
        {
            "de" => CultureInfo.GetCultureInfo("de-DE"), "es" => CultureInfo.GetCultureInfo("es-ES"),
            "fr" => CultureInfo.GetCultureInfo("fr-FR"), "it" => CultureInfo.GetCultureInfo("it-IT"),
            "pt-br" => CultureInfo.GetCultureInfo("pt-BR"), _ => CultureInfo.GetCultureInfo("en-US")
        };
        try { return value.ToString(T("format.dateTime"), culture); }
        catch (FormatException) { return value.ToString("g", culture); }
    }

    private void Fail(Exception exception) { Status = exception is RecoveryOperationException failure ? T($"operations.outcome.{failure.Outcome}") + ": " + exception.Message : exception.Message; _services.Logs.Write("Database", exception.Message, LogSeverity.Error); }
}

public sealed class SelectableDatabase(string name, Action selectionChanged) : ViewModelBase
{
    private bool _isSelected;
    public string Name { get; } = name;
    public bool IsSelected { get => _isSelected; set { if (Set(ref _isSelected, value)) selectionChanged(); } }
}

public sealed record DatabaseBackupListItem(DatabaseBackupInfo Backup, string CreatedAtLabel)
{
    public string Path => Backup.Path;
    public string DatabaseName => Backup.DatabaseName;
    public string FileName => Backup.FileName;
    public string SizeLabel => Backup.SizeLabel;
}
