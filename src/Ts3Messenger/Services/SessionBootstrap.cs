using System.Linq;
using System.Net.Http;
using System.Windows;
using Ts3Messenger.Models;

namespace Ts3Messenger.Services;

public enum SignInAttemptResult
{
    Success,
    NetworkOrServer,
    Unauthorized,
    NoCredentials,
}

public sealed class SignInSuccess
{
    public required string ServerBaseUrl { get; init; }
    public required Ts3ApiClient Api { get; init; }
    public required UserMe Me { get; init; }
    public required MessageCryptoService Crypto { get; init; }
    public required KeyStore Keys { get; init; }
}

public static class SessionBootstrap
{
    public static async Task<(SignInAttemptResult Result, SignInSuccess? Success)> TrySignInAsync(
        string? serverBaseUrl = null,
        string? loginId = null,
        string? password = null)
    {
        var settings = AppSettingsStore.Load();
        var baseUrl = string.IsNullOrWhiteSpace(serverBaseUrl)
            ? settings.ServerBaseUrl.Trim()
            : serverBaseUrl.Trim();

        if (string.IsNullOrWhiteSpace(baseUrl))
            return (SignInAttemptResult.NoCredentials, null);

        var saved = SessionCookieStore.Load();
        if (saved != null
            && !string.IsNullOrWhiteSpace(saved.SessionCookie)
            && Uri.TryCreate(saved.ServerBaseUrl, UriKind.Absolute, out var savedUri)
            && string.Equals(
                savedUri.GetLeftPart(UriPartial.Authority),
                baseUrl,
                StringComparison.OrdinalIgnoreCase))
        {
            var api = Ts3ApiClient.Create(baseUrl, settings.TrustServerCertificate);
            api.RestoreCookies(saved.SessionCookie, saved.CsrfCookie);
            try
            {
                var me = await api.MeAsync();
                return (SignInAttemptResult.Success, await FinishSignInAsync(baseUrl, api, me));
            }
            catch (Exception ex)
            {
                api.Dispose();
                var kind = ClassifySignInFailure(ex);
                if (kind == SignInAttemptResult.Unauthorized)
                    SessionCookieStore.Clear();
                if (kind != SignInAttemptResult.NetworkOrServer)
                    return (kind, null);
            }
        }

        var useRemembered = loginId == null && password == null;
        if (useRemembered)
        {
            if (!settings.RememberUserInfo || string.IsNullOrWhiteSpace(settings.LoginId))
                return (SignInAttemptResult.NoCredentials, null);

            loginId = settings.LoginId.Trim();
            password = CredentialProtector.Unprotect(settings.ProtectedPasswordB64);
            if (string.IsNullOrEmpty(password))
                return (SignInAttemptResult.NoCredentials, null);
        }

        var signInApi = Ts3ApiClient.Create(baseUrl, settings.TrustServerCertificate);
        try
        {
            var me = await signInApi.LoginAsync(loginId!.Trim(), password!);
            return (SignInAttemptResult.Success, await FinishSignInAsync(baseUrl, signInApi, me));
        }
        catch (Exception ex)
        {
            signInApi.Dispose();
            return (ClassifySignInFailure(ex), null);
        }
    }

    private static async Task<SignInSuccess> FinishSignInAsync(string baseUrl, Ts3ApiClient api, UserMe me)
    {
        var keys = new KeyStore();
        var crypto = new MessageCryptoService(keys);
        try
        {
            await api.PutPublicKeyAsync(crypto.ExportPublicKeyB64());
        }
        catch
        {
            // key may already exist
        }

        await ChatTimestamps.TryLoadFromServerAsync(api);

        return new SignInSuccess
        {
            ServerBaseUrl = baseUrl,
            Api = api,
            Me = me,
            Crypto = crypto,
            Keys = keys,
        };
    }

    private static SignInAttemptResult ClassifySignInFailure(Exception ex)
    {
        if (IsUnauthorized(ex))
            return SignInAttemptResult.Unauthorized;

        if (ex is HttpRequestException
            or TaskCanceledException
            or OperationCanceledException
            or UriFormatException
            or System.Net.Sockets.SocketException)
            return SignInAttemptResult.NetworkOrServer;

        if (ex.InnerException != null)
            return ClassifySignInFailure(ex.InnerException);

        return SignInAttemptResult.NetworkOrServer;
    }

    private static bool IsUnauthorized(Exception ex)
    {
        if (ex is HttpRequestException http)
        {
            var msg = http.Message;
            if (msg.Contains("401", StringComparison.Ordinal)
                || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return ex.InnerException != null && IsUnauthorized(ex.InnerException);
    }

    public static MainWindow OpenMainWindow(SignInSuccess success, bool startInTrayOnly)
    {
        CloseLoginWindows();
        success.Keys.Persist();
        var main = new MainWindow(success.ServerBaseUrl, success.Api, success.Me, success.Crypto, success.Keys);
        if (startInTrayOnly)
        {
            main.EnableSilentTrayStartup();
            main.PrepareHiddenTrayStart();
            main.Show();
            main.Hide();
        }
        else
            main.Show();
        return main;
    }

    public static void CloseLoginWindows()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var login in Application.Current.Windows.OfType<LoginWindow>().ToList())
                login.CloseAfterSignIn();
        });
    }

    public static void ShowLoginWindow(bool retryUntilServerReturns = false)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = Application.Current.Windows.OfType<LoginWindow>().FirstOrDefault();
            if (existing != null)
            {
                if (retryUntilServerReturns)
                    existing.EnableServerRecoveryRetry();
                if (!existing.IsVisible)
                    existing.Show();
                existing.Activate();
                return;
            }

            var login = new LoginWindow();
            if (retryUntilServerReturns)
                login.EnableServerRecoveryRetry();
            login.Show();
        });
    }
}
