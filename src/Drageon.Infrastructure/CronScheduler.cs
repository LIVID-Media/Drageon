using System.Collections.Concurrent;
using System.Diagnostics;
using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed class CronScheduler(
    IPortablePathService paths,
    JsonFileStore store,
    IAppLogService logs,
    ILanguageService? language = null) : ICronScheduler
{
    private const int InlineOutputLimit = 500;
    private readonly string _jobsPath = paths.Resolve("app", "config", "cronjobs.json");
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly SemaphoreSlim _missedJobsGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, byte> _runningJobs = new();
    private HashSet<Guid> _missedJobIds = [];
    private volatile bool _missedJobsHandled;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;

    public event EventHandler<CronJobRunResult>? JobFinished;
    public bool IsRunning => _lifetime is { IsCancellationRequested: false };

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) return;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        logs.Write("Cron", T("scheduler.started", "Interner Scheduler gestartet."));
        _missedJobIds = await FindMissedJobIdsAsync(_lifetime.Token).ConfigureAwait(false);
        _loop = RunLoopAsync(_lifetime.Token);
    }

    public async Task RunMissedJobsAsync(CancellationToken cancellationToken = default)
    {
        await _missedJobsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_missedJobsHandled) return;
            var jobs = await ReadJobsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var job in jobs.Where(job => _missedJobIds.Contains(job.Id) && job.Enabled && job.CatchUpMissedRun))
            {
                await ExecuteJobAsync(job, cancellationToken).ConfigureAwait(false);
            }
            _missedJobsHandled = true;
        }
        finally
        {
            _missedJobsGate.Release();
        }
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        logs.Write("Cron", T("scheduler.reloaded", "Cronjob-Konfiguration neu geladen."));
        return Task.CompletedTask;
    }

    public async Task RunNowAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = (await ReadJobsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.Id == jobId)
            ?? throw new InvalidOperationException(T("scheduler.jobMissing", "Der Cronjob wurde nicht gefunden."));
        await ExecuteJobAsync(job, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_lifetime is null) return;
        _lifetime.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
        _lifetime = null;
        logs.Write("Cron", T("scheduler.stopped", "Interner Scheduler beendet."));
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        await RunDueJobsAsync(DateTimeOffset.Now, cancellationToken).ConfigureAwait(false);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            await RunDueJobsAsync(DateTimeOffset.Now, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RunDueJobsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);
        foreach (var job in await ReadJobsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!job.Enabled || !Matches(job, minute)) continue;
            if (!_missedJobsHandled && _missedJobIds.Contains(job.Id)) continue;
            if (job.LastRunAt is not null && job.LastRunAt.Value >= minute) continue;
            _ = ExecuteJobAsync(job, cancellationToken);
        }
    }

    private async Task<HashSet<Guid>> FindMissedJobIdsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        var result = new HashSet<Guid>();
        foreach (var job in await ReadJobsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!job.Enabled || !job.CatchUpMissedRun || job.LastRunAt is null) continue;
            var from = job.LastRunAt.Value.AddMinutes(1);
            if (from < now.AddDays(-7)) from = now.AddDays(-7);
            var missed = false;
            for (var cursor = new DateTimeOffset(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0, from.Offset);
                 cursor <= now; cursor = cursor.AddMinutes(1))
            {
                if (!Matches(job, cursor)) continue;
                missed = true;
                break;
            }
            if (missed) result.Add(job.Id);
        }
        return result;
    }

    private async Task ExecuteJobAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        if (!_runningJobs.TryAdd(job.Id, 0)) return;
        var success = false;
        var message = T("scheduler.executed", "Ausgeführt.");
        try
        {
            logs.Write("Cron", TF("scheduler.starting", "Starte '{0}' ({1}).", job.Name, job.Type));
            var output = job.Type switch
            {
                ScheduledJobType.PhpScript => await RunPhpAsync(job, cancellationToken).ConfigureAwait(false),
                ScheduledJobType.Url => await RunUrlAsync(job, cancellationToken).ConfigureAwait(false),
                ScheduledJobType.Curl => await RunCurlAsync(job, cancellationToken).ConfigureAwait(false),
                ScheduledJobType.Command => await RunCommandAsync(job, cancellationToken).ConfigureAwait(false),
                _ => throw new NotSupportedException(TF("scheduler.typeUnsupported", "Cronjob-Typ {0} wird nicht unterstützt.", job.Type))
            };
            message = await StoreOutputAsync(job, output, CancellationToken.None).ConfigureAwait(false);
            success = true;
            logs.Write("Cron", TF("scheduler.success", "'{0}' erfolgreich: {1}", job.Name, message));
        }
        catch (Exception exception)
        {
            message = await StoreOutputAsync(job, exception.Message, CancellationToken.None).ConfigureAwait(false);
            logs.Write("Cron", TF("scheduler.failed", "'{0}' fehlgeschlagen: {1}", job.Name, message), LogSeverity.Error);
        }
        finally
        {
            _runningJobs.TryRemove(job.Id, out _);
            var finished = DateTimeOffset.Now;
            await UpdateLastRunAsync(job.Id, finished, CancellationToken.None).ConfigureAwait(false);
            JobFinished?.Invoke(this, new CronJobRunResult(job.Id, job.Name, finished, success, message));
        }
    }

    private async Task<string> StoreOutputAsync(ScheduledJob job, string output, CancellationToken cancellationToken)
    {
        var isLargeOutput = output.Length > InlineOutputLimit;
        var relativePath = $"app/var/logs/cron/{job.Id:N}.log";
        var outputPath = paths.Resolve("app", "var", "logs", "cron", $"{job.Id:N}.log");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath,
                $"[{DateTimeOffset.Now:O}] {job.Name}{Environment.NewLine}{Environment.NewLine}{output}",
                cancellationToken).ConfigureAwait(false);
            return isLargeOutput
                ? TF("scheduler.outputSaved", "Ausgabe mit {0} Zeichen wurde gespeichert: {1}", output.Length, relativePath)
                : output;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return isLargeOutput ? output[..InlineOutputLimit] + "…" : output;
        }
    }

    private bool Matches(ScheduledJob job, DateTimeOffset time)
    {
        try
        {
            return CronExpression.IsMatch(job.Expression, time, T("scheduler.expressionFields", "Cron-Ausdruck benötigt fünf Felder: Minute Stunde Tag Monat Wochentag."));
        }
        catch (FormatException exception)
        {
            logs.Write("Cron", TF("scheduler.invalidExpression", "'{0}' hat einen ungültigen Cron-Ausdruck: {1}", job.Name, exception.Message), LogSeverity.Error);
            return false;
        }
    }

    private async Task<string> RunPhpAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        var active = await ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        if (!active.TryGetValue("php", out var version)) throw new InvalidOperationException(T("scheduler.phpMissing", "Keine PHP-Version aktiviert."));
        var phpRoot = paths.Resolve("bin", "php", version);
        var executable = Path.Combine(phpRoot, "php.exe");
        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = phpRoot + Path.PathSeparator + currentPath,
            ["PHPRC"] = PhpVersionConfiguration.GetGeneratedDirectory(paths, version)
        };
        return await RunProcessAsync(
            executable,
            $"\"{ResolvePortableTarget(job.Target)}\" {job.Arguments}".Trim(),
            cancellationToken,
            environment).ConfigureAwait(false);
    }

    private static async Task<string> RunUrlAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var response = await client.GetAsync(job.Target, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return $"HTTP {(int)response.StatusCode}";
    }

    private async Task<string> RunCurlAsync(ScheduledJob job, CancellationToken cancellationToken)
    {
        var active = await ReadActiveAsync(cancellationToken).ConfigureAwait(false);
        if (!active.TryGetValue("curl", out var version)) throw new InvalidOperationException(T("scheduler.curlMissing", "Keine cURL-Version aktiviert."));
        var directory = paths.Resolve("bin", "curl", version);
        var executable = Directory.EnumerateFiles(directory, "curl.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException(T("scheduler.curlExecutableMissing", "curl.exe wurde in der aktiven cURL-Version nicht gefunden."));
        return await RunProcessAsync(executable, $"{job.Arguments} \"{job.Target}\"".Trim(), cancellationToken).ConfigureAwait(false);
    }

    private Task<string> RunCommandAsync(ScheduledJob job, CancellationToken cancellationToken) =>
        RunProcessAsync(ResolvePortableTarget(job.Target), job.Arguments, cancellationToken);

    private async Task<string> RunProcessAsync(
        string executable,
        string arguments,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException(T("scheduler.executableMissing", "Programmdatei nicht gefunden."), executable);
        var info = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            WorkingDirectory = paths.AppRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (environment is not null)
        {
            foreach (var variable in environment) info.Environment[variable.Key] = variable.Value;
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException(T("scheduler.processStartFailed", "Cronjob-Prozess konnte nicht gestartet werden."));
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = (await outputTask.ConfigureAwait(false)).Trim();
        var error = (await errorTask.ConfigureAwait(false)).Trim();
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Length > 0 ? error : $"Exitcode {process.ExitCode}");
        return output.Length > 0 ? output : "Exitcode 0";
    }

    private string ResolvePortableTarget(string target)
    {
        if (Path.IsPathRooted(target)) throw new InvalidOperationException(T("scheduler.relativePaths", "Cronjob-Pfade müssen relativ zum portablen App-Ordner sein."));
        return paths.Resolve(target.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries));
    }

    private async Task<IReadOnlyList<ScheduledJob>> ReadJobsAsync(CancellationToken cancellationToken) =>
        await store.ReadAsync<List<ScheduledJob>>(_jobsPath, cancellationToken).ConfigureAwait(false) ?? [];

    private async Task<Dictionary<string, string>> ReadActiveAsync(CancellationToken cancellationToken) =>
        await store.ReadAsync<Dictionary<string, string>>(paths.Resolve("app", "config", "packages.active.json"), cancellationToken).ConfigureAwait(false) ?? [];

    private async Task UpdateLastRunAsync(Guid id, DateTimeOffset finished, CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var jobs = (await store.ReadAsync<List<ScheduledJob>>(_jobsPath, cancellationToken).ConfigureAwait(false) ?? [])
                .Select(job => job.Id == id ? job with { LastRunAt = finished } : job).ToList();
            await store.WriteAsync(_jobsPath, jobs, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private string T(string key, string fallback) => language?.Get(key, fallback) ?? fallback;
    private string TF(string key, string fallback, params object?[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, T(key, fallback), arguments);
}

internal static class CronExpression
{
    public static bool IsMatch(string expression, DateTimeOffset time, string? invalidFieldMessage = null)
    {
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5) throw new FormatException(invalidFieldMessage ?? "Cron expression requires five fields: minute hour day month weekday.");
        return MatchField(fields[0], time.Minute, 0, 59)
            && MatchField(fields[1], time.Hour, 0, 23)
            && MatchField(fields[2], time.Day, 1, 31)
            && MatchField(fields[3], time.Month, 1, 12)
            && MatchField(fields[4], (int)time.DayOfWeek, 0, 7, true);
    }

    private static bool MatchField(string field, int value, int minimum, int maximum, bool sundayAlias = false)
    {
        foreach (var item in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var stepParts = item.Split('/', 2);
            var step = stepParts.Length == 2 && int.TryParse(stepParts[1], out var parsedStep) && parsedStep > 0 ? parsedStep : 1;
            var range = stepParts[0];
            int start;
            int end;
            if (range == "*") { start = minimum; end = maximum; }
            else if (range.Contains('-'))
            {
                var bounds = range.Split('-', 2);
                if (!int.TryParse(bounds[0], out start) || !int.TryParse(bounds[1], out end)) continue;
            }
            else if (int.TryParse(range, out var exact)) { start = exact; end = exact; }
            else continue;

            var compared = sundayAlias && value == 0 && start == 7 ? 7 : value;
            if (compared >= start && compared <= end && (compared - start) % step == 0) return true;
        }
        return false;
    }
}
