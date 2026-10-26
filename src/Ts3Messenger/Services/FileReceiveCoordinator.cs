namespace Ts3Messenger.Services;

/// <summary>Serializes inbound LAN file receives so concurrent punches do not share one TCP client.</summary>
internal sealed class FileReceiveCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action)
    {
        await _gate.WaitAsync();
        try
        {
            return await action();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RunExclusiveAsync(Func<Task> action)
    {
        await _gate.WaitAsync();
        try
        {
            await action();
        }
        finally
        {
            _gate.Release();
        }
    }
}
