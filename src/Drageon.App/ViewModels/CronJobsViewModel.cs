using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Drageon.Core;

namespace Drageon.App.ViewModels;

public sealed class CronJobsViewModel : ViewModelBase, IRefreshable, IDisposable
{
    private readonly AppServices _services;
    private CronJobItemViewModel? _selectedJob;
    private Guid? _editingId;
    private DateTimeOffset? _lastRunAt;
    private string _name = "";
    private ScheduledJobType _selectedType = ScheduledJobType.PhpScript;
    private string _expression = "*/5 * * * *";
    private string _minute = "*/5";
    private string _hour = "*";
    private string _dayOfMonth = "*";
    private string _month = "*";
    private string _dayOfWeek = "*";
    private bool _synchronizingExpression;
    private string _target = "";
    private string _arguments = "";
    private bool _enabled = true;
    private bool _catchUpMissedRun;
    private string _status;

    public CronJobsViewModel(AppServices services)
    {
        _services = services;
        _status = T("cron.status.runtimeOnly");
        Types =
        [
            new(ScheduledJobType.PhpScript, T("cron.type.php")),
            new(ScheduledJobType.Url, T("cron.type.url")),
            new(ScheduledJobType.Curl, T("cron.type.curl")),
            new(ScheduledJobType.Command, T("cron.type.command"))
        ];
        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync());
        NewCommand = new RelayCommand(_ => NewJob());
        SaveCommand = new AsyncRelayCommand(_ => SaveAsync());
        DeleteCommand = new AsyncRelayCommand(_ => DeleteAsync());
        RunNowCommand = new AsyncRelayCommand(_ => RunNowAsync());
        BrowseTargetCommand = new RelayCommand(_ => BrowseTarget(), _ => CanBrowseTarget);
        OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder());
        PreviewOutputCommand = new RelayCommand(_ => PreviewOutput(), _ => CanPreviewOutput);
        _services.Cron.JobFinished += OnJobFinished;
        _ = RefreshAsync();
    }

    public ObservableCollection<CronJobItemViewModel> Jobs { get; } = [];
    public IReadOnlyList<ScheduledJobTypeOption> Types { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand NewCommand { get; }
    public AsyncRelayCommand SaveCommand { get; }
    public AsyncRelayCommand DeleteCommand { get; }
    public AsyncRelayCommand RunNowCommand { get; }
    public RelayCommand BrowseTargetCommand { get; }
    public RelayCommand OpenOutputFolderCommand { get; }
    public RelayCommand PreviewOutputCommand { get; }

    public CronJobItemViewModel? SelectedJob
    {
        get => _selectedJob;
        set
        {
            if (!Set(ref _selectedJob, value) || value is null) return;
            LoadEditor(value.Job);
        }
    }

    public string Name { get => _name; set => Set(ref _name, value); }
    public ScheduledJobType SelectedType
    {
        get => _selectedType;
        set
        {
            if (!Set(ref _selectedType, value)) return;
            Raise(nameof(CanBrowseTarget));
            BrowseTargetCommand.RaiseCanExecuteChanged();
        }
    }
    public ScheduledJobTypeOption SelectedTypeOption
    {
        get => Types.First(item => item.Type == SelectedType);
        set
        {
            if (value is not null && Set(ref _selectedType, value.Type, nameof(SelectedType)))
            {
                Raise();
                Raise(nameof(CanBrowseTarget));
                BrowseTargetCommand.RaiseCanExecuteChanged();
            }
        }
    }
    public string Expression
    {
        get => _expression;
        set
        {
            if (!Set(ref _expression, value) || _synchronizingExpression) return;
            ReadExpressionParts(value);
        }
    }
    public string Minute { get => _minute; set { if (Set(ref _minute, value)) WriteExpression(); } }
    public string Hour { get => _hour; set { if (Set(ref _hour, value)) WriteExpression(); } }
    public string DayOfMonth { get => _dayOfMonth; set { if (Set(ref _dayOfMonth, value)) WriteExpression(); } }
    public string Month { get => _month; set { if (Set(ref _month, value)) WriteExpression(); } }
    public string DayOfWeek { get => _dayOfWeek; set { if (Set(ref _dayOfWeek, value)) WriteExpression(); } }
    public string Target { get => _target; set => Set(ref _target, value); }
    public bool CanBrowseTarget => SelectedType is ScheduledJobType.PhpScript or ScheduledJobType.Command;
    public bool CanPreviewOutput => _editingId is not null && File.Exists(GetOutputFile(_editingId.Value));
    public string Arguments { get => _arguments; set => Set(ref _arguments, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool CatchUpMissedRun { get => _catchUpMissedRun; set => Set(ref _catchUpMissedRun, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }

    public async Task RefreshAsync()
    {
        var jobs = await ReadJobsAsync();
        var previous = _editingId;
        Jobs.Clear();
        foreach (var job in jobs.OrderBy(static item => item.Name)) Jobs.Add(new CronJobItemViewModel(job));
        SelectedJob = previous is null ? Jobs.FirstOrDefault() : Jobs.FirstOrDefault(item => item.Job.Id == previous) ?? Jobs.FirstOrDefault();
    }

    public void Dispose() => _services.Cron.JobFinished -= OnJobFinished;

    private void NewJob()
    {
        _editingId = null;
        _lastRunAt = null;
        _selectedJob = null;
        Raise(nameof(SelectedJob));
        Name = "";
        SelectedType = ScheduledJobType.PhpScript;
        Expression = "*/5 * * * *";
        Target = "";
        Arguments = "";
        Enabled = true;
        CatchUpMissedRun = false;
        Status = T("cron.status.configureNew");
        PreviewOutputCommand.RaiseCanExecuteChanged();
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Target) || Expression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 5)
        {
            Status = T("cron.status.validation");
            return;
        }

        var jobs = await ReadJobsAsync();
        var id = _editingId ?? Guid.NewGuid();
        var job = new ScheduledJob
        {
            Id = id,
            Name = Name.Trim(),
            Type = SelectedType,
            Expression = Expression.Trim(),
            Target = Target.Trim(),
            Arguments = Arguments.Trim(),
            Enabled = Enabled,
            CatchUpMissedRun = CatchUpMissedRun,
            LastRunAt = _lastRunAt
        };
        var index = jobs.FindIndex(item => item.Id == id);
        if (index >= 0) jobs[index] = job; else jobs.Add(job);
        await _services.Store.WriteAsync(JobsFile, jobs);
        _editingId = id;
        await _services.Cron.ReloadAsync();
        await RefreshAsync();
        Status = TF("cron.status.saved", job.Name);
        PreviewOutputCommand.RaiseCanExecuteChanged();
    }

    private async Task DeleteAsync()
    {
        if (_editingId is null) return;
        var result = System.Windows.MessageBox.Show(TF("cron.confirm.delete", Name), T("cron.action.delete"),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;
        var jobs = await ReadJobsAsync();
        jobs.RemoveAll(item => item.Id == _editingId.Value);
        await _services.Store.WriteAsync(JobsFile, jobs);
        _editingId = null;
        await _services.Cron.ReloadAsync();
        await RefreshAsync();
        Status = T("cron.status.deleted");
        PreviewOutputCommand.RaiseCanExecuteChanged();
    }

    private async Task RunNowAsync()
    {
        if (_editingId is null)
        {
            Status = T("cron.status.saveFirst");
            return;
        }
        Status = TF("cron.status.running", Name);
        await _services.Cron.RunNowAsync(_editingId.Value);
    }

    private void BrowseTarget()
    {
        if (!CanBrowseTarget) return;
        var currentFile = GetTargetFile();
        var initialDirectory = currentFile is null
            ? _services.Paths.Resolve("www")
            : Path.GetDirectoryName(currentFile) ?? _services.Paths.Resolve("www");
        Directory.CreateDirectory(initialDirectory);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = T("cron.browseTitle"),
            InitialDirectory = initialDirectory,
            FileName = currentFile is null ? "" : Path.GetFileName(currentFile),
            CheckFileExists = true,
            Multiselect = false,
            Filter = SelectedType == ScheduledJobType.PhpScript
                ? T("cron.browseFilter.php")
                : T("cron.browseFilter.command")
        };
        if (dialog.ShowDialog() != true) return;

        var fullPath = Path.GetFullPath(dialog.FileName);
        var relative = Path.GetRelativePath(_services.Paths.AppRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            Status = T("cron.error.outsideApp");
            return;
        }
        Target = relative.Replace('\\', '/');
    }

    private string? GetTargetFile()
    {
        if (string.IsNullOrWhiteSpace(Target)) return null;
        try
        {
            var target = Target.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.IsPathRooted(target)
                ? target
                : Path.Combine(_services.Paths.AppRoot, target));
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void OpenOutputFolder()
    {
        var directory = _services.Paths.Resolve("app", "var", "logs", "cron");
        try
        {
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
            Status = T("cron.status.outputFolderOpened");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            Status = TF("cron.status.outputFolderFailed", exception.Message);
        }
    }

    private void PreviewOutput()
    {
        if (_editingId is null) return;
        var file = GetOutputFile(_editingId.Value);
        if (!File.Exists(file))
        {
            Status = T("cron.status.noOutput");
            PreviewOutputCommand.RaiseCanExecuteChanged();
            return;
        }
        try
        {
            new CronOutputWindow
            {
                DataContext = new CronOutputPreviewViewModel(
                    TF("cron.outputPreview.title", Name),
                    File.ReadAllText(file)),
                Owner = System.Windows.Application.Current.MainWindow
            }.Show();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Status = TF("cron.status.outputReadFailed", exception.Message);
        }
    }

    private string GetOutputFile(Guid id) =>
        _services.Paths.Resolve("app", "var", "logs", "cron", $"{id:N}.log");

    private void LoadEditor(ScheduledJob job)
    {
        _editingId = job.Id;
        _lastRunAt = job.LastRunAt;
        Name = job.Name;
        SelectedType = job.Type;
        Expression = job.Expression;
        Target = job.Target;
        Arguments = job.Arguments;
        Enabled = job.Enabled;
        CatchUpMissedRun = job.CatchUpMissedRun;
        PreviewOutputCommand.RaiseCanExecuteChanged();
    }

    private void OnJobFinished(object? sender, CronJobRunResult result)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            Status = result.Success
                ? TF("cron.status.success", result.JobName, result.Message)
                : TF("cron.status.failed", result.JobName, result.Message);
            await RefreshAsync();
            PreviewOutputCommand.RaiseCanExecuteChanged();
        });
    }

    private string JobsFile => _services.Paths.Resolve("app", "config", "cronjobs.json");
    private async Task<List<ScheduledJob>> ReadJobsAsync() => await _services.Store.ReadAsync<List<ScheduledJob>>(JobsFile) ?? [];

    private void ReadExpressionParts(string expression)
    {
        var parts = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5) return;
        _synchronizingExpression = true;
        try
        {
            Minute = parts[0];
            Hour = parts[1];
            DayOfMonth = parts[2];
            Month = parts[3];
            DayOfWeek = parts[4];
        }
        finally { _synchronizingExpression = false; }
    }

    private void WriteExpression()
    {
        if (_synchronizingExpression) return;
        _synchronizingExpression = true;
        try { Expression = $"{Minute} {Hour} {DayOfMonth} {Month} {DayOfWeek}"; }
        finally { _synchronizingExpression = false; }
    }
}

public sealed class CronJobItemViewModel
{
    public CronJobItemViewModel(ScheduledJob job) => Job = job;

    public ScheduledJob Job { get; }
    public string Name => Job.Name;
    public string Details => $"{Job.Expression} · {Localization.Get($"cron.type.{Job.Type.ToString().ToLowerInvariant()}")} · {(Job.Enabled ? Localization.Get("state.active") : Localization.Get("state.paused"))}";
    public string LastRun => Job.LastRunAt?.LocalDateTime.ToString("g") ?? Localization.Get("cron.neverRun");
}

public sealed record ScheduledJobTypeOption(ScheduledJobType Type, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed record CronOutputPreviewViewModel(string Title, string Content);
