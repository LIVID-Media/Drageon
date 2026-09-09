using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Drageon.Core;

namespace Drageon.App.ViewModels;

public sealed class LogsViewModel : ViewModelBase, IRefreshable, IDisposable
{
    private readonly AppServices _services;
    private readonly List<LogEntry> _allEntries = [];
    private readonly Dictionary<string, string> _componentDisplayNames;
    private LogComponentOption _selectedComponent = null!;
    private LogSeverityOption _selectedSeverity;
    private string _filterText = "";
    private string _status = "";

    public LogsViewModel(AppServices services)
    {
        _services = services;
        _componentDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Application"] = "Application",
            ["PHP"] = "PHP",
            ["MariaDB"] = "MariaDB",
            ["MySQL"] = "MySQL",
            ["phpMyAdmin"] = "phpMyAdmin",
            ["cURL"] = "cURL",
            ["Cron"] = "Cron",
            ["Mail"] = T("nav.mail")
        };
        foreach (var server in _services.WebServers.All.OrderBy(server => server.DisplayName))
        {
            _componentDisplayNames.TryAdd(server.Id, server.DisplayName);
            if (server.PhpRuntimeProcessId is not null)
                _componentDisplayNames.TryAdd(server.PhpRuntimeProcessId, "PHP FastCGI");
        }
        ComponentOptions = [];
        SeverityOptions =
        [
            new(LogSeverityFilter.All, T("logs.filter.all")),
            new(LogSeverityFilter.WarningAndError, T("logs.filter.warningError")),
            new(LogSeverityFilter.Error, T("logs.filter.errors"))
        ];
        _selectedSeverity = SeverityOptions[1];
        ClearCommand = new RelayCommand(_ => Clear());
        CopyVisibleLogsCommand = new RelayCommand(_ => CopyVisibleLogs());
        OpenLogFolderCommand = new RelayCommand(_ => OpenLogFolder());
        DeleteLogFilesCommand = new AsyncRelayCommand(_ => DeleteLogFilesAsync());
        _services.Logs.EntryAdded += OnEntryAdded;
        _allEntries.AddRange(_services.Logs.GetRecent());
        RebuildComponentOptions();
        ApplyFilter();
    }

    public ObservableCollection<LogEntryRow> Entries { get; } = [];
    public ObservableCollection<LogComponentOption> ComponentOptions { get; }
    public ObservableCollection<LogSeverityOption> SeverityOptions { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyVisibleLogsCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public AsyncRelayCommand DeleteLogFilesCommand { get; }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public LogComponentOption SelectedComponent
    {
        get => _selectedComponent;
        set { if (Set(ref _selectedComponent, value)) ApplyFilter(); }
    }

    public string FilterText
    {
        get => _filterText;
        set { if (Set(ref _filterText, value)) ApplyFilter(); }
    }

    public LogSeverityOption SelectedSeverity
    {
        get => _selectedSeverity;
        set { if (Set(ref _selectedSeverity, value)) ApplyFilter(); }
    }

    public Task RefreshAsync()
    {
        _allEntries.Clear();
        _allEntries.AddRange(_services.Logs.GetRecent());
        RebuildComponentOptions();
        ApplyFilter();
        return Task.CompletedTask;
    }

    private void OnEntryAdded(object? sender, LogEntry entry)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _allEntries.Add(entry);
            if (_allEntries.Count > 5000) _allEntries.RemoveAt(0);
            AddComponentOption(entry.Component);
            if (Matches(entry))
            {
                Entries.Add(new LogEntryRow(entry));
                while (Entries.Count > 1000) Entries.RemoveAt(0);
            }
        });
    }

    public void Dispose() => _services.Logs.EntryAdded -= OnEntryAdded;

    private void ApplyFilter()
    {
        Entries.Clear();
        foreach (var entry in _allEntries.Where(Matches).TakeLast(1000)) Entries.Add(new LogEntryRow(entry));
    }

    private bool Matches(LogEntry entry) =>
        (SelectedComponent.Id is null || entry.Component.Equals(SelectedComponent.Id, StringComparison.OrdinalIgnoreCase))
        && (SelectedSeverity.Filter == LogSeverityFilter.All
            || SelectedSeverity.Filter == LogSeverityFilter.WarningAndError && entry.Severity is LogSeverity.Warning or LogSeverity.Error
            || SelectedSeverity.Filter == LogSeverityFilter.Error && entry.Severity == LogSeverity.Error)
        && (FilterText.Length == 0
            || entry.Component.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || entry.Message.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
            || entry.Timestamp.ToString("dd.MM HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)
                .Contains(FilterText, StringComparison.OrdinalIgnoreCase));

    private void Clear()
    {
        _services.Logs.Clear();
        _allEntries.Clear();
        RebuildComponentOptions();
        ApplyFilter();
        Status = T("logs.status.cleared");
    }

    private void RebuildComponentOptions()
    {
        var selectedId = _selectedComponent?.Id;
        var allComponents = new LogComponentOption(null, T("logs.filter.allComponents"));
        var options = _allEntries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Component))
            .Select(entry => new LogComponentOption(entry.Component, GetComponentDisplayName(entry.Component)))
            .DistinctBy(static option => option.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static option => option.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ComponentOptions.Clear();
        ComponentOptions.Add(allComponents);
        foreach (var option in options) ComponentOptions.Add(option);

        _selectedComponent = ComponentOptions.FirstOrDefault(option =>
            option.Id?.Equals(selectedId, StringComparison.OrdinalIgnoreCase) == true) ?? allComponents;
        Raise(nameof(SelectedComponent));
    }

    private void AddComponentOption(string component)
    {
        if (string.IsNullOrWhiteSpace(component)
            || ComponentOptions.Any(option => option.Id?.Equals(component, StringComparison.OrdinalIgnoreCase) == true)) return;

        var option = new LogComponentOption(component, GetComponentDisplayName(component));
        var index = 1;
        while (index < ComponentOptions.Count
            && StringComparer.CurrentCultureIgnoreCase.Compare(ComponentOptions[index].DisplayName, option.DisplayName) <= 0) index++;
        ComponentOptions.Insert(index, option);
    }

    private string GetComponentDisplayName(string component) =>
        _componentDisplayNames.GetValueOrDefault(component, component);

    private void CopyVisibleLogs()
    {
        if (Entries.Count == 0)
        {
            Status = T("logs.status.nothingToCopy");
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(string.Join(Environment.NewLine, Entries.Select(static item => item.Text)));
            Status = TF("logs.status.copied", Entries.Count);
        }
        catch (ExternalException)
        {
            Status = T("logs.status.clipboardBusy");
        }
    }

    private void OpenLogFolder()
    {
        var logRoot = _services.Paths.Resolve("app", "var", "logs");
        try
        {
            Directory.CreateDirectory(logRoot);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logRoot}\"") { UseShellExecute = true });
            Status = T("logs.status.folderOpened");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            Status = TF("logs.status.folderFailed", exception.Message);
        }
    }

    private async Task DeleteLogFilesAsync()
    {
        var result = System.Windows.MessageBox.Show(
            T("logs.confirm.delete"),
            T("logs.deleteFiles"),
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        var logRoot = _services.Paths.Resolve("app", "var", "logs");
        var deleted = 0;
        var unavailable = 0;

        await Task.Run(() =>
        {
            _services.Logs.Clear();
            if (!Directory.Exists(logRoot)) return;

            foreach (var file in Directory.EnumerateFiles(logRoot, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (IOException)
                {
                    unavailable++;
                }
                catch (UnauthorizedAccessException)
                {
                    unavailable++;
                }
            }
        });

        _allEntries.Clear();
        Entries.Clear();
        Status = unavailable == 0
            ? TF("logs.status.deleted", deleted)
            : TF("logs.status.deletedPartial", deleted, unavailable);
    }
}

public sealed class LogEntryRow(LogEntry entry)
{
    public string Text { get; } = $"[{entry.Timestamp:dd.MM HH:mm:ss}] [{entry.Component}] {entry.Message}";
    public LogSeverity Severity { get; } = entry.Severity;
}

public sealed record LogComponentOption(string? Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public enum LogSeverityFilter { All, WarningAndError, Error }

public sealed record LogSeverityOption(LogSeverityFilter Filter, string DisplayName)
{
    public override string ToString() => DisplayName;
}

