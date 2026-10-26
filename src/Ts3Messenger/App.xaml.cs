using System.Windows;
using System.Windows.Threading;
using Ts3Messenger.Services;
using Ts3Messenger.Windows;

namespace Ts3Messenger;

public partial class App : Application
{
    private SingleInstanceCoordinator? _singleInstance;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.Error("AppDomain 미처리 예외", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("UnobservedTask", args.Exception);
            args.SetObserved();
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        var text = UserErrorMessages.ForUi(args.Exception, "처리되지 않은 오류");
        AppDialog.Show(text, "TS3 Messenger", MessageBoxButton.OK, MessageBoxImage.Error);
        args.Handled = true;
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        ThemeService.Initialize(this);
        _singleInstance = SingleInstanceCoordinator.TryAcquirePrimary();
        if (_singleInstance == null)
        {
            Shutdown();
            return;
        }

        _singleInstance.StartActivationListener(() =>
            TrayApplicationService.Instance.ShowPrimaryWindow());

        AppIcon.NotifyShell();
        TrayApplicationService.Instance.Initialize();
        TrayApplicationService.Instance.EnableRunAtStartupOnce();
        var windowsStartup = e.Args.Any(a =>
            string.Equals(a, "--startup", StringComparison.OrdinalIgnoreCase));
        _ = RunStartupAsync(windowsStartup);
    }

    private void Application_Exit(object sender, ExitEventArgs e)
    {
        TrayApplicationService.Instance.Dispose();
        _singleInstance?.Dispose();
        _singleInstance = null;
    }

    private Task RunStartupAsync(bool windowsStartup)
    {
        AppLog.Info(windowsStartup
            ? "앱 시작 (Windows 자동 실행, 트레이)"
            : "앱 시작 (사용자 실행)");
        BackgroundSignInService.Instance.StartForAppLaunch(windowsStartup);
        return Task.CompletedTask;
    }
}
