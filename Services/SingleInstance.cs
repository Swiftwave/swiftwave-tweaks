using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SwiftwaveTweaks.Services;

/// <summary>
/// Per-user single-instance guard.
///
/// The first instance owns a session-local named mutex and listens on a local named pipe.
/// A second launch detects the owned mutex, asks the existing instance to come to the
/// foreground over the pipe, and exits before any WPF window is created.
///
/// No service, helper process, lock file or registry entry is involved. The mutex lives in
/// the "Local\" session namespace and the pipe name carries the Windows session id, so the
/// guard works per-user without elevation.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\SwiftwaveTweaks.SingleInstance";

    private static Mutex? _mutex;
    private static CancellationTokenSource? _listenerCancellation;

    // The pipe name is stable for the user+session so a second launch can find the listener;
    // the first instance hands back its PID over the pipe for AllowSetForegroundWindow.
    private static string StablePipeName
        => $"swiftwave-tweaks-activate-user-{Environment.UserName}-session-{System.Diagnostics.Process.GetCurrentProcess().SessionId}";

    /// <summary>True for the first instance (mutex acquired); false when one is already running.</summary>
    public static bool TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, createdNew: out bool createdNew);
        if (createdNew)
        {
            _mutex = mutex;
            return true;
        }
        mutex.Dispose();
        return false;
    }

    /// <summary>
    /// Second launch: connect to the running instance, grant it foreground permission for
    /// this user-initiated launch, and send the activation byte. Never throws.
    /// </summary>
    public static void SignalExisting()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", StablePipeName, PipeDirection.InOut, PipeOptions.None);
                client.Connect(1500);
                using var reader = new BinaryReader(client, Encoding.UTF8, leaveOpen: true);
                int ownerPid = reader.ReadInt32();
                // The Explorer-launched second process is allowed to hand foreground rights
                // to the first instance (bounded by the OS foreground-lock rules).
                try { AllowSetForegroundWindow(ownerPid); } catch { }
                client.WriteByte(1);
                client.Flush();
                return;
            }
            catch (IOException) { }
            catch (TimeoutException) { }
            catch (InvalidOperationException) { }
            Thread.Sleep(120);
        }
    }

    /// <summary>First instance: accept activation signals until shutdown. Never throws to callers.</summary>
    public static void StartListener(Action onActivate)
    {
        _listenerCancellation = new CancellationTokenSource();
        var token = _listenerCancellation.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream server;
                try
                {
                    server = new NamedPipeServerStream(
                        StablePipeName, PipeDirection.InOut, maxNumberOfServerInstances: 4,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    try { await Task.Delay(500, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    continue;
                }

                try
                {
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    try
                    {
                        var pidBytes = BitConverter.GetBytes(Environment.ProcessId);
                        await server.WriteAsync(pidBytes, token).ConfigureAwait(false);
                        await server.FlushAsync(token).ConfigureAwait(false);
                        var one = new byte[1];
                        int read = await server.ReadAsync(one, token).ConfigureAwait(false);
                        if (read > 0)
                            onActivate();
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception) { /* a half-open client must never kill the loop */ }
                }
                catch (OperationCanceledException) { try { server.Dispose(); } catch { } break; }
                finally { try { server.Dispose(); } catch { } }
            }
        }, token);
    }

    /// <summary>Stops the pipe loop and releases the mutex; safe to call more than once.</summary>
    public static void Shutdown()
    {
        try { _listenerCancellation?.Cancel(); } catch { }
        _listenerCancellation?.Dispose();
        _listenerCancellation = null;
        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
}
