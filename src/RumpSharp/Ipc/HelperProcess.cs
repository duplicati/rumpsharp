using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;

namespace RumpSharp.Ipc;

/// <summary>
/// Runs <c>rumpsharp-helper</c> as a long-lived child process and exchanges NDJSON with it.
/// </summary>
/// <remarks>
/// <para>
/// The helper is launched directly - not through <c>open</c> - because LaunchServices detaches
/// stdio, which would leave no pipe to talk over. Launching the binary from inside the bundle is
/// enough for macOS to give the child the bundle's identity.
/// </para>
/// <para>
/// stdout carries protocol only and is parsed line by line; stderr carries diagnostics only and is
/// never parsed. A line that cannot be parsed is reported and skipped rather than ending the
/// session.
/// </para>
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed class HelperProcess : IDisposable
{
    /// <summary>How long to wait for the helper's <c>ready</c> message before giving up on it.</summary>
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long a helper gets to exit on its own after being asked to shut down.</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How many stderr lines to keep, to explain a crash after the fact.</summary>
    private const int DiagnosticHistory = 20;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _executable;
    private readonly Action<string> _diagnostics;

    /// <summary>Requests waiting for a <c>result</c>, keyed by correlation id.</summary>
    private readonly ConcurrentDictionary<int, TaskCompletionSource<HelperMessage>> _pending = new();

    private readonly Queue<string> _recent = new();
    private readonly Lock _recentGate = new();

    /// <summary>Serialises writes to the helper's stdin, which is a single shared pipe.</summary>
    private readonly Lock _writeGate = new();

    private TaskCompletionSource<HelperMessage> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Process? _process;
    private int _nextRequestId;
    private volatile bool _stopping;

    /// <param name="executable">Full path of the helper inside the <c>.app</c> bundle.</param>
    /// <param name="diagnostics">Sink for anything the helper says on stderr, plus protocol problems.</param>
    internal HelperProcess(string executable, Action<string> diagnostics)
    {
        _executable = executable;
        _diagnostics = diagnostics;
    }

    /// <summary>Raised for every unsolicited <c>response</c>: the user interacted with a notification.</summary>
    internal event Action<HelperMessage>? ResponseReceived;

    /// <summary>Whether a helper is currently running.</summary>
    internal bool IsRunning => _process is { HasExited: false };

    /// <summary>Starts the helper and waits for its <c>ready</c> message.</summary>
    /// <returns>The <c>ready</c> message, which reports the identity macOS gave the helper.</returns>
    /// <exception cref="NotificationException">The helper could not be started or never became ready.</exception>
    internal HelperMessage Start()
    {
        var info = new ProcessStartInfo(_executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
            UseShellExecute = false,

            // Inside the bundle, so a relative path in a diagnostic still means something.
            WorkingDirectory = Path.GetDirectoryName(_executable)!,
        };

        _stopping = false;
        _ready = new TaskCompletionSource<HelperMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

        Process process;
        try
        {
            process = Process.Start(info)
                ?? throw new NotificationException($"Could not start the notification helper '{_executable}'.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new NotificationException(
                $"Could not start the notification helper '{_executable}': {e.Message}", e);
        }

        _process = process;
        _process.StandardInput.AutoFlush = true;
        _process.StandardInput.NewLine = "\n";

        Pump(process.StandardOutput, ReadProtocol);
        Pump(process.StandardError, ReadDiagnostics);

        if (!_ready.Task.Wait(StartTimeout))
        {
            var recent = Recent();
            Dispose();
            throw new NotificationException(
                "The notification helper did not report itself ready within "
                + $"{StartTimeout.TotalSeconds:0} seconds.{recent}");
        }

        return _ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>Sends a request and waits for the matching answer.</summary>
    /// <param name="request">The request; its <see cref="HelperRequest.RequestId"/> is assigned here.</param>
    /// <param name="timeout">How long to wait. Use <see cref="Timeout.InfiniteTimeSpan"/> to wait forever.</param>
    /// <returns>The answer, or <see langword="null"/> if it did not arrive in time.</returns>
    /// <exception cref="NotificationException">The helper exited before answering.</exception>
    internal HelperMessage? Request(HelperRequest request, TimeSpan timeout)
    {
        var id = Interlocked.Increment(ref _nextRequestId);
        request.RequestId = id;

        var completion = new TaskCompletionSource<HelperMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        try
        {
            Post(request);

            if (!completion.Task.Wait(timeout))
            {
                return null;
            }

            return completion.Task.GetAwaiter().GetResult();
        }
        catch (AggregateException e) when (e.InnerException is not null)
        {
            // Unwrap the failure the reader thread published when the helper died.
            throw e.InnerException;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a request without waiting for an answer.</summary>
    /// <exception cref="NotificationException">The helper is not running.</exception>
    internal void Post(HelperRequest request)
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            throw new NotificationException($"The notification helper is not running.{Recent()}");
        }

        Write(process, request);
    }

    /// <summary>Writes one NDJSON line to a helper's stdin.</summary>
    /// <remarks>The lock matters: stdin is one pipe shared by every caller.</remarks>
    private void Write(Process process, HelperRequest request)
    {
        var line = HelperJson.Serialize(request);

        lock (_writeGate)
        {
            try
            {
                process.StandardInput.WriteLine(line);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
            {
                throw new NotificationException(
                    $"The notification helper closed its input pipe.{Recent()}", e);
            }
        }
    }

    /// <summary>Asks the helper to exit, then makes sure it did.</summary>
    public void Dispose()
    {
        var process = Interlocked.Exchange(ref _process, null);
        if (process is null)
        {
            return;
        }

        _stopping = true;

        try
        {
            Write(process, new HelperRequest { Type = HelperProtocol.Requests.Shutdown });
        }
        catch (NotificationException)
        {
            // Already gone, or the pipe is broken; closing stdin below covers it either way.
        }

        try
        {
            // Closing stdin is the backstop: the helper terminates on EOF even if it never read the
            // shutdown request.
            process.StandardInput.Close();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }

        try
        {
            if (!process.WaitForExit((int)ShutdownTimeout.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or SystemException)
        {
        }

        process.Dispose();
        FailPending(new NotificationException("The notification helper was shut down."));
    }

    // ------------------------------------------------------------------ reader threads

    /// <summary>Drains one of the helper's output streams on a dedicated background thread.</summary>
    private static void Pump(StreamReader reader, Action<StreamReader> body)
    {
        var thread = new Thread(() =>
        {
            try
            {
                body(reader);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // The pipe went away with the process; the protocol reader reports that separately.
            }
        })
        {
            IsBackground = true,
            Name = "rumpsharp-helper-io",
        };

        thread.Start();
    }

    private void ReadProtocol(StreamReader stdout)
    {
        while (stdout.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (HelperJson.Deserialize(line) is not { Type: not null } message)
            {
                // One bad line must never end the session.
                Report($"ignoring an unreadable line from the helper: {Truncate(line)}");
                continue;
            }

            Dispatch(message);
        }

        // EOF: the helper is gone. Nothing that is waiting will ever be answered.
        var failure = _stopping
            ? new NotificationException("The notification helper was shut down.")
            : new NotificationException($"The notification helper exited unexpectedly.{Recent()}");

        _ready.TrySetException(failure);
        FailPending(failure);

        if (!_stopping)
        {
            Report("the helper exited unexpectedly");
        }
    }

    private void ReadDiagnostics(StreamReader stderr)
    {
        while (stderr.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            lock (_recentGate)
            {
                _recent.Enqueue(line);
                while (_recent.Count > DiagnosticHistory)
                {
                    _recent.Dequeue();
                }
            }

            Report(line);
        }
    }

    private void Dispatch(HelperMessage message)
    {
        switch (message.Type)
        {
            case HelperProtocol.Messages.Ready:
                _ready.TrySetResult(message);
                return;

            case HelperProtocol.Messages.Result:
                if (message.RequestId is { } id && _pending.TryRemove(id, out var completion))
                {
                    completion.TrySetResult(message);
                }
                else
                {
                    // An answer to a request that has already timed out.
                    Report($"discarding a late answer to request {message.RequestId}");
                }

                return;

            case HelperProtocol.Messages.Response:
                ResponseReceived?.Invoke(message);
                return;

            case HelperProtocol.Messages.Error:
                Report($"{message.Context ?? "helper"}: {message.Message}");
                return;

            default:
                Report($"ignoring an unknown '{message.Type}' message from the helper");
                return;
        }
    }

    private void FailPending(Exception failure)
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var completion))
            {
                completion.TrySetException(failure);
            }
        }
    }

    /// <summary>The helper's most recent stderr output, for appending to an error message.</summary>
    private string Recent()
    {
        lock (_recentGate)
        {
            return _recent.Count == 0 ? string.Empty : " Its output was: " + string.Join(" / ", _recent);
        }
    }

    private void Report(string message)
    {
        try
        {
            _diagnostics(message);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            // A broken diagnostics sink must not take the reader thread down with it.
        }
    }

    private static string Truncate(string line) =>
        line.Length <= 200 ? line : string.Concat(line.AsSpan(0, 200), "...");
}
