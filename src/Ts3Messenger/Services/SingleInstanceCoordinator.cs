using System.Threading;

namespace Ts3Messenger.Services;

/// <summary>
/// Ensures one process per user. A second launch signals the running instance to show its window.
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\Ts3Messenger.SingleInstance";
    private const string ActivateEventName = @"Local\Ts3Messenger.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private CancellationTokenSource? _listenerCts;

    private SingleInstanceCoordinator(Mutex mutex, EventWaitHandle activateEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
    }

    public static SingleInstanceCoordinator? TryAcquirePrimary()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            mutex.Dispose();
            return null;
        }

        var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        return new SingleInstanceCoordinator(mutex, activate);
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
            activate.Set();
        }
        catch (Exception)
        {
            // Primary still starting or event missing; ignore.
        }
    }

    public void StartActivationListener(Action onActivate)
    {
        _listenerCts = new CancellationTokenSource();
        var token = _listenerCts.Token;
        _ = Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (!_activateEvent.WaitOne(TimeSpan.FromMilliseconds(500)))
                        continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (token.IsCancellationRequested)
                    break;

                try
                {
                    onActivate();
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"기존 인스턴스 활성화 실패: {ex.Message}");
                }
            }
        }, token);
    }

    public void Dispose()
    {
        _listenerCts?.Cancel();
        _listenerCts?.Dispose();
        _listenerCts = null;
        _activateEvent.Dispose();
        try
        {
            _mutex.ReleaseMutex();
        }
        catch
        {
            // ignore
        }

        _mutex.Dispose();
    }
}
