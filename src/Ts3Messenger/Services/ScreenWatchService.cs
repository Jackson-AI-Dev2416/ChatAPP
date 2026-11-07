namespace Ts3Messenger.Services;

/// <summary>Periodic full-desktop capture upload while the user is signed in.</summary>
public sealed class ScreenWatchService : IDisposable
{
    private readonly Ts3ApiClient _api;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private int _intervalSeconds = 20;
    private bool _captureEnabled = true;
    private int _uploading;

    private const int PolicyPollSeconds = 60;

    public ScreenWatchService(Ts3ApiClient api) => _api = api;

    public void Start()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            _ = RunAsync(_cts.Token);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        await RefreshPolicyOnceAsync();
        _ = PollPolicyLoopAsync(ct);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CaptureAndUploadAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"화면 감시: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task RefreshPolicyOnceAsync()
    {
        try
        {
            var policy = await _api.GetScreenCapturePolicyAsync();
            _captureEnabled = policy.CaptureEnabled;
            if (policy.IntervalSeconds >= 5)
                _intervalSeconds = policy.IntervalSeconds;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"화면 감시 policy: {ex.Message}");
        }
    }

    private async Task PollPolicyLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PolicyPollSeconds), ct);
                await RefreshPolicyOnceAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"화면 감시 policy poll: {ex.Message}");
            }
        }
    }

    private async Task CaptureAndUploadAsync(CancellationToken ct)
    {
        if (!_captureEnabled)
            return;
        if (Interlocked.Exchange(ref _uploading, 1) == 1)
            return;
        try
        {
            var frames = await Task.Run(ScreenWatchCaptureHelper.CaptureAllMonitorsJpeg, ct);
            if (frames.Count == 0)
                return;
            var capturedAt = DateTime.UtcNow;
            await _api.UploadScreenCapturesAsync(capturedAt, frames);
        }
        finally
        {
            Interlocked.Exchange(ref _uploading, 0);
        }
    }
}
