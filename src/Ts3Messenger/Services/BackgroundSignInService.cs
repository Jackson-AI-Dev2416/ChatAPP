using System.Windows;

namespace Ts3Messenger.Services;

/// <summary>App launch: one sign-in attempt. After server disconnect: 10s retry until restored.</summary>
public sealed class BackgroundSignInService
{
    private static BackgroundSignInService? _instance;
    public static BackgroundSignInService Instance => _instance ??= new();

    private readonly object _gate = new();
    private CancellationTokenSource? _recoveryCts;
    private bool _credentialsBlocked;
    private bool _serverRecoveryActive;

    private const int ServerRecoveryIntervalSeconds = 10;

    public void StartForAppLaunch(bool windowsStartup)
    {
        StopRecovery();
        _ = TrySignInOnceAsync(startInTrayOnly: windowsStartup, fromRecovery: false);
    }

    public void BeginServerRecovery(bool showMainWindowWhenRestored)
    {
        lock (_gate)
        {
            StopRecovery();
            _serverRecoveryActive = true;
            _recoveryCts = new CancellationTokenSource();
            _ = RunServerRecoveryAsync(showMainWindowWhenRestored, _recoveryCts.Token);
        }
    }

    public void StopRecovery()
    {
        lock (_gate)
        {
            _serverRecoveryActive = false;
            _recoveryCts?.Cancel();
            _recoveryCts = null;
        }
    }

    public void ClearCredentialsBlock() => _credentialsBlocked = false;

    private static bool HasMainWindow() =>
        Application.Current.Dispatcher.Invoke(() =>
            Application.Current.Windows.OfType<MainWindow>().Any(w => w.IsLoaded));

    private async Task TrySignInOnceAsync(bool startInTrayOnly, bool fromRecovery)
    {
        if (TrayApplicationService.Instance.ExitRequested || HasMainWindow())
            return;

        if (_credentialsBlocked)
        {
            TrayApplicationService.Instance.SetTrayStatus("Open to sign in (saved password rejected)");
            return;
        }

        if (fromRecovery)
            TrayApplicationService.Instance.SetTrayStatus("waiting for server");

        var (result, success) = await SessionBootstrap.TrySignInAsync();

        if (result == SignInAttemptResult.Success && success != null)
        {
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (HasMainWindow())
                    return;
                SessionBootstrap.OpenMainWindow(success, startInTrayOnly: startInTrayOnly);
            });
            TrayApplicationService.Instance.SetTrayStatus(null);
            if (fromRecovery)
                StopRecovery();
            return;
        }

        if (result == SignInAttemptResult.Unauthorized)
        {
            _credentialsBlocked = true;
            AppLog.Warn("자동 로그인: 저장된 비밀번호가 거부됨(401). 트레이 Open으로 수동 로그인.");
            if (fromRecovery)
                StopRecovery();
        }
        else if (result == SignInAttemptResult.NoCredentials)
            TrayApplicationService.Instance.SetTrayStatus("Open to sign in");
        else if (!fromRecovery)
            TrayApplicationService.Instance.SetTrayStatus("Open to sign in");
    }

    private async Task RunServerRecoveryAsync(bool showMainWindowWhenRestored, CancellationToken ct)
    {
        var startInTrayOnly = !showMainWindowWhenRestored;

        while (!ct.IsCancellationRequested && _serverRecoveryActive)
        {
            try
            {
                if (TrayApplicationService.Instance.ExitRequested)
                    return;

                if (HasMainWindow())
                {
                    TrayApplicationService.Instance.SetTrayStatus(null);
                    StopRecovery();
                    return;
                }

                if (_credentialsBlocked)
                {
                    TrayApplicationService.Instance.SetTrayStatus("Open to sign in (saved password rejected)");
                    StopRecovery();
                    return;
                }

                await TrySignInOnceAsync(startInTrayOnly, fromRecovery: true);

                if (HasMainWindow())
                    return;

                await Task.Delay(ServerRecoveryIntervalSeconds * 1000, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                AppLog.Warn($"서버 복구 재로그인: {ex.Message}");
                try
                {
                    await Task.Delay(ServerRecoveryIntervalSeconds * 1000, ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
