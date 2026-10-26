using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Ts3Messenger.Services;

namespace Ts3Messenger;

public partial class LoginWindow : Window
{
    public LoginWindow()
    {
        InitializeComponent();
        AppIcon.ApplyToWindow(this);
        LogoBrush.ImageSource = AppIcon.Icon;
        UpdateThemeGlyph();
        ThemeService.Changed += UpdateThemeGlyph;
        Closed += (_, _) =>
        {
            ThemeService.Changed -= UpdateThemeGlyph;
            StopServerRetry();
        };
        Closing += OnWindowClosing;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(LoginBox.Text)) LoginBox.Focus();
            else if (string.IsNullOrEmpty(PasswordBox.Password)) PasswordBox.Focus();
            else SignInButton.Focus();
        };
        var s = AppSettingsStore.Load();
        ServerBox.Text = s.ServerBaseUrl;
        RememberCheck.IsChecked = s.RememberUserInfo;
        if (s.RememberUserInfo)
        {
            LoginBox.Text = s.LoginId;
            var pw = CredentialProtector.Unprotect(s.ProtectedPasswordB64);
            if (!string.IsNullOrEmpty(pw)) PasswordBox.Password = pw;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e) => ThemeService.Toggle();

    private void UpdateThemeGlyph() => ThemeToggleButton.Content = ThemeService.IsDark ? "\uE706" : "\uE708";

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        if (!SignInButton.IsEnabled)
            return;
        ErrorText.Text = "";
        SignInButton.IsEnabled = false;
        SignInButton.Content = "Signing in…";
        StopServerRetry();
        try
        {
            var settings = AppSettingsStore.Load();
            settings.ServerBaseUrl = ServerBox.Text.Trim();
            settings.RememberUserInfo = RememberCheck.IsChecked == true;
            if (settings.RememberUserInfo)
            {
                settings.LoginId = LoginBox.Text.Trim();
                settings.ProtectedPasswordB64 = CredentialProtector.Protect(PasswordBox.Password);
            }
            else
            {
                settings.LoginId = "";
                settings.ProtectedPasswordB64 = null;
            }
            AppSettingsStore.Save(settings);

            BackgroundSignInService.Instance.ClearCredentialsBlock();
            var (result, success) = await SessionBootstrap.TrySignInAsync(
                settings.ServerBaseUrl,
                LoginBox.Text.Trim(),
                PasswordBox.Password);
            if (result != SignInAttemptResult.Success || success == null)
            {
                ErrorText.Text = result switch
                {
                    SignInAttemptResult.Unauthorized => "Invalid login ID or password.",
                    SignInAttemptResult.NetworkOrServer =>
                        "Cannot reach the server. Check address, port, firewall, and Docker/proxy.",
                    _ => "Could not sign in. Check server URL and saved credentials.",
                };
                if (result == SignInAttemptResult.NetworkOrServer && _retryBecauseServerDown)
                    EnableServerRecoveryRetry();
                return;
            }

            SessionBootstrap.OpenMainWindow(success, startInTrayOnly: false);
            PrepareForRealClose();
            Close();
        }
        catch (Exception ex)
        {
            ErrorText.Text = UserErrorMessages.ForUi(ex, "sign in");
        }
        finally
        {
            SignInButton.IsEnabled = true;
            SignInButton.Content = "Sign in";
        }
    }

    private bool _allowRealClose;
    private bool _retryBecauseServerDown;
    private CancellationTokenSource? _serverRetryCts;

    /// <summary>Shown because the server dropped while the chat window was open. Retries every 10 seconds.</summary>
    public void EnableServerRecoveryRetry()
    {
        _retryBecauseServerDown = true;
        if (_serverRetryCts != null)
            return;
        _serverRetryCts = new CancellationTokenSource();
        _ = RunServerRetryAsync(_serverRetryCts.Token);
    }

    private void StopServerRetry()
    {
        _serverRetryCts?.Cancel();
        _serverRetryCts = null;
    }

    private async Task RunServerRetryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (SignInButton.IsEnabled)
                await TrySilentSignInAsync();
            if (ct.IsCancellationRequested)
                return;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TrySilentSignInAsync()
    {
        if (!SignInButton.IsEnabled)
            return;
        ErrorText.Text = "Waiting for the server…";
        try
        {
            var (result, success) = await SessionBootstrap.TrySignInAsync();
            if (result != SignInAttemptResult.Success || success == null)
            {
                if (result == SignInAttemptResult.Unauthorized)
                {
                    StopServerRetry();
                    ErrorText.Text = "Saved password was rejected. Sign in manually.";
                }
                return;
            }

            StopServerRetry();
            var stayInTray = !IsVisible;
            SessionBootstrap.OpenMainWindow(success, startInTrayOnly: stayInTray);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"로그인 창 자동 재시도: {ex.Message}");
        }
    }

    public void CloseAfterSignIn()
    {
        StopServerRetry();
        PrepareForRealClose();
        Close();
    }

    private void PrepareForRealClose() => _allowRealClose = true;

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowRealClose || TrayApplicationService.Instance.ExitRequested)
            return;
        e.Cancel = true;
        TrayApplicationService.Instance.TryHideWindowOnUserClose(this);
    }
}
