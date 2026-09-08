using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Drageon.App;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private readonly Mutex _mutex;
    private readonly bool _ownsMutex;

    public SingleInstanceCoordinator(string appRoot)
    {
        var normalizedRoot = Path.GetFullPath(appRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        var identifier = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot)))[..16];
        PipeName = $"Drageon.{identifier}";
        _mutex = new Mutex(true, $"Local\\{PipeName}", out _ownsMutex);
    }

    public bool IsPrimaryInstance => _ownsMutex;
    public string PipeName { get; }

    public async Task ListenAsync(Func<string, Task> handler, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, Encoding.UTF8, true, 1024, true);
                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(command)) await handler(command).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task<bool> SendAsync(string command, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 20 && !cancellationToken.IsCancellationRequested; attempt++)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    PipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                using var attemptLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptLifetime.CancelAfter(TimeSpan.FromMilliseconds(350));
                await pipe.ConnectAsync(attemptLifetime.Token).ConfigureAwait(false);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, true)
                {
                    AutoFlush = true
                };
                await writer.WriteLineAsync(command.AsMemory(), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (TimeoutException)
            {
            }

            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    public void Dispose()
    {
        if (_ownsMutex)
        {
            try { _mutex.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _mutex.Dispose();
    }
}
