using Microsoft.Win32;

namespace Ts3Messenger.Services;

public static class WindowsStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Ts3Messenger";

    public static void Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
            return;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.SetValue(ValueName, $"\"{exe}\" --startup");
            AppLog.Info("Windows 시작 프로그램에 등록됨");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"시작 프로그램 등록 실패: {ex.Message}");
        }
    }
}
