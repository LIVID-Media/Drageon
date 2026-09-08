using System.Collections.Concurrent;
using System.Diagnostics;
using Drageon.Core;

namespace Drageon.Infrastructure;

public sealed class ProcessManager(IAppLogService? logs = null, ILanguageService? language = null) : IProcessManager
{
    private readonly ConcurrentDictionary<string, Process> _processes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProcessStatus> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _stopping = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<ProcessStatus>? StatusChanged;

    public IReadOnlyCollection<ProcessStatus> GetStatuses() => _statuses.Values.ToArray();

    public async Task<ProcessStatus> StartAsync(ProcessDefinition definition, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_processes.TryGetValue(definition.Id, out var existing) && !existing.HasExited)
        {
            return Update(new ProcessStatus(definition.Id, ComponentState.Running, existing.Id));
        }

        if (!File.Exists(definition.ExecutablePath))
        {
            var error = TF("process.executableMissing", "Programmdatei nicht gefunden: {0}", definition.ExecutablePath);
            logs?.Write(definition.Id, error, LogSeverity.Error);
            return Update(new ProcessStatus(definition.Id, ComponentState.Failed, Error: error));
        }

        Update(new ProcessStatus(definition.Id, ComponentState.Starting));
        logs?.Write(definition.Id, TF("process.starting", "Starte {0} {1}", Path.GetFileName(definition.ExecutablePath), definition.Arguments).TrimEnd());
        try
        {
            Directory.CreateDirectory(definition.WorkingDirectory);
            if (definition.LogFile is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(definition.LogFile)!);
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = definition.ExecutablePath,
                Arguments = definition.Arguments,
                WorkingDirectory = definition.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            if (definition.EnvironmentVariables is not null)
            {
                foreach (var variable in definition.EnvironmentVariables)
                {
                    startInfo.Environment[variable.Key] = variable.Value;
                }
            }

            var process = new Process
            {
                EnableRaisingEvents = true,
                StartInfo = startInfo
            };
            var processJob = definition.KillChildrenOnExit && OperatingSystem.IsWindows() ? new WindowsProcessJob() : null;
            var lifecycleLock = new object();
            var exitCompletion = new TaskCompletionSource<ProcessStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, args) => AppendProcessLine(definition, args.Data, LogSeverity.Information);
            process.ErrorDataReceived += (_, args) => AppendProcessLine(definition, args.Data, ClassifySeverity(definition.Id, args.Data));
            process.Exited += (_, _) =>
            {
                lock (lifecycleLock) processJob?.Dispose();
                var exitCode = process.ExitCode;
                _processes.TryRemove(definition.Id, out _);
                var wasStopped = _stopping.TryRemove(definition.Id, out _);
                var status = exitCode == 0 || wasStopped
                    ? new ProcessStatus(definition.Id, ComponentState.Stopped)
                    : new ProcessStatus(definition.Id, ComponentState.Failed, Error: TF("process.exitFailed", "Prozess mit Exitcode {0} beendet. Siehe Logs.", exitCode));
                logs?.Write(definition.Id, status.Error ?? T("process.stopped", "Prozess beendet."), status.State == ComponentState.Stopped ? LogSeverity.Information : LogSeverity.Error);
                Update(status);
                exitCompletion.TrySetResult(status);
            };
            lock (lifecycleLock)
            {
                try
                {
                    process.Start();
                    processJob?.Assign(process);
                    _processes[definition.Id] = process;
                }
                catch
                {
                    processJob?.Dispose();
                    try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
                    throw;
                }
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var completed = await Task.WhenAny(exitCompletion.Task, Task.Delay(750, cancellationToken)).ConfigureAwait(false);
            if (completed == exitCompletion.Task) return await exitCompletion.Task.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited) return await exitCompletion.Task.ConfigureAwait(false);

            var running = Update(new ProcessStatus(definition.Id, ComponentState.Running, process.Id));
            logs?.Write(definition.Id, TF("process.started", "Gestartet (PID {0}).", process.Id));
            return running;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logs?.Write(definition.Id, exception.Message, LogSeverity.Error);
            return Update(new ProcessStatus(definition.Id, ComponentState.Failed, Error: exception.Message));
        }
    }

    public async Task StopAsync(string id, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!_processes.TryGetValue(id, out var process) || process.HasExited)
        {
            Update(new ProcessStatus(id, ComponentState.Stopped));
            return;
        }

        Update(new ProcessStatus(id, ComponentState.Stopping, process.Id));
        _stopping[id] = 0;
        logs?.Write(id, T("process.stopping", "Wird beendet …"));
        try
        {
            if (!process.CloseMainWindow()) process.Kill(true);
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(true);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAllAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        foreach (var id in _processes.Keys.ToArray())
        {
            await StopAsync(id, timeout, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private ProcessStatus Update(ProcessStatus status)
    {
        _statuses[status.Id] = status;
        StatusChanged?.Invoke(this, status);
        return status;
    }

    private string T(string key, string fallback) => language?.Get(key, fallback) ?? fallback;
    private string TF(string key, string fallback, params object?[] arguments) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, T(key, fallback), arguments);

    private void AppendProcessLine(ProcessDefinition definition, string? line, LogSeverity severity)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        logs?.Write(definition.Id, line, severity);
        if (definition.LogFile is null) return;
        try
        {
            lock (_statuses)
            {
                File.AppendAllText(definition.LogFile, $"[{DateTimeOffset.Now:O}] {line}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
    }

    private static LogSeverity ClassifySeverity(string componentId, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return LogSeverity.Information;
        if (line.Contains("[Note]", StringComparison.OrdinalIgnoreCase)
            || line.Contains(":notice]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("[Information]", StringComparison.OrdinalIgnoreCase)) return LogSeverity.Information;
        if (line.Contains("[Warning]", StringComparison.OrdinalIgnoreCase)
            || line.Contains(":warn]", StringComparison.OrdinalIgnoreCase)
            || line.Contains(": Warning:", StringComparison.OrdinalIgnoreCase)
            || line.Contains("PHP Warning", StringComparison.OrdinalIgnoreCase)) return LogSeverity.Warning;
        if (line.Contains("[Error]", StringComparison.OrdinalIgnoreCase)
            || line.Contains(":error]", StringComparison.OrdinalIgnoreCase)
            || line.Contains(":crit]", StringComparison.OrdinalIgnoreCase)
            || line.Contains(":alert]", StringComparison.OrdinalIgnoreCase)
            || line.Contains(":emerg]", StringComparison.OrdinalIgnoreCase)
            || line.Contains("fatal", StringComparison.OrdinalIgnoreCase)) return LogSeverity.Error;
        return componentId.Equals("apache", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("mariadb", StringComparison.OrdinalIgnoreCase)
            || componentId.Equals("mysql", StringComparison.OrdinalIgnoreCase)
            ? LogSeverity.Information
            : LogSeverity.Error;
    }
}
