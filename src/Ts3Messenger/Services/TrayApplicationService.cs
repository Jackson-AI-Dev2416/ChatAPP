using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using DrawingIcon = System.Drawing.Icon;

namespace Ts3Messenger.Services;

public sealed class TrayApplicationService : IDisposable
{
    private static TrayApplicationService? _instance;
    public static TrayApplicationService Instance => _instance ??= new();

    private NotifyIcon? _notify;
    private DrawingIcon? _plainIcon;
    private DrawingIcon? _unreadIcon;
    private bool _unreadAttention;
    private bool _disposed;
    private string? _pendingPeerId;
    private string? _statusSuffix;

    public bool ExitRequested { get; private set; }

    public void Initialize()
    {
        if (_notify != null)
            return;

        _plainIcon = AppIcon.CreateNotifyIcon();
        _notify = new NotifyIcon
        {
            Text = "TS3 Messenger",
            Icon = _plainIcon,
            Visible = true,
        };
        _notify.DoubleClick += (_, _) => ShowPrimaryWindow();
        _notify.BalloonTipClicked += (_, _) => ShowPrimaryWindow();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowPrimaryWindow());
        menu.Items.Add("Exit", null, (_, _) => RequestExit());
        _notify.ContextMenuStrip = menu;
        ShowHotkey.Start();
    }

    public void EnableRunAtStartupOnce()
    {
        WindowsStartupRegistration.Enable();
    }

    public void SetUnreadAttention(bool unread)
    {
        Initialize();
        if (_notify == null || _plainIcon == null)
            return;
        if (_unreadAttention != unread)
        {
            _unreadAttention = unread;
            _unreadIcon ??= AppIcon.CreateNotifyIcon(unread: true);
            _notify.Icon = unread ? _unreadIcon : _plainIcon;
        }

        ApplyTrayText();
    }

    public void SetTrayStatus(string? statusSuffix)
    {
        Initialize();
        _statusSuffix = string.IsNullOrWhiteSpace(statusSuffix) ? null : statusSuffix.Trim();
        ApplyTrayText();
    }

    private void ApplyTrayText()
    {
        if (_notify == null)
            return;
        if (_unreadAttention)
            _notify.Text = "TS3 Messenger — new message";
        else if (_statusSuffix != null)
            _notify.Text = TruncateTrayText($"TS3 Messenger — {_statusSuffix}");
        else
            _notify.Text = "TS3 Messenger";
    }

    private static string TruncateTrayText(string text) =>
        text.Length <= 63 ? text : text[..60] + "...";

    public void ShowIncomingMessageNotification(string senderDisplayName, string preview, string senderPeerId)
    {
        Initialize();
        _pendingPeerId = senderPeerId;
        var title = string.IsNullOrWhiteSpace(senderDisplayName) ? "New message" : senderDisplayName;
        var body = string.IsNullOrWhiteSpace(preview) ? "You have a new message." : preview;
        try
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch
        {
            // optional
        }

        try
        {
            _notify?.ShowBalloonTip(
                5000,
                title,
                body,
                ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"알림 표시 실패: {ex.Message}");
        }
    }

    public string? TakePendingPeerId()
    {
        var id = _pendingPeerId;
        _pendingPeerId = null;
        return id;
    }

    public bool TryHideWindowOnUserClose(Window window)
    {
        if (ExitRequested)
            return false;

        Initialize();
        window.Hide();
        return true;
    }

    public void TogglePrimaryWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var main = System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
            if (main != null && IsForegroundWindow(main))
            {
                TryHideWindowOnUserClose(main);
                return;
            }

            var login = System.Windows.Application.Current.Windows.OfType<LoginWindow>().FirstOrDefault();
            if (login != null && IsForegroundWindow(login))
            {
                TryHideWindowOnUserClose(login);
                return;
            }

            ShowPrimaryWindow();
        });
    }

    public void ShowPrimaryWindow()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var main = System.Windows.Application.Current.Windows.OfType<MainWindow>().FirstOrDefault();
            if (main != null)
            {
                main.ShowFromTray();
                BringToFront(main);
                _ = main.TryOpenPendingPeerFromNotificationAsync();
                return;
            }

            var login = System.Windows.Application.Current.Windows.OfType<LoginWindow>().FirstOrDefault();
            if (login != null)
            {
                BringToFront(login);
                return;
            }

            SessionBootstrap.ShowLoginWindow();
        });
    }

    public void RequestExit()
    {
        ExitRequested = true;
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (Window w in System.Windows.Application.Current.Windows)
            {
                if (w is MainWindow mw)
                    mw.PrepareForRealClose();
                w.Close();
            }

            Dispose();
            System.Windows.Application.Current.Shutdown();
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ShowHotkey.Stop();
        if (_notify != null)
        {
            _notify.Visible = false;
            _notify.Icon = null;
            _notify.Dispose();
            _notify = null;
        }

        _plainIcon?.Dispose();
        _unreadIcon?.Dispose();
        _plainIcon = null;
        _unreadIcon = null;
    }

    private static bool IsForegroundWindow(Window window)
    {
        if (!window.IsVisible || window.WindowState == WindowState.Minimized)
            return false;
        var handle = new WindowInteropHelper(window).Handle;
        return handle != IntPtr.Zero && GetForegroundWindow() == handle;
    }

    private static void BringToFront(Window window)
    {
        if (window is MainWindow mw)
            mw.ShowFromTray();
        else
            window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        keybd_event(0x12, 0, 0, UIntPtr.Zero);
        keybd_event(0x12, 0, 2, UIntPtr.Zero);
        SetForegroundWindow(handle);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}

static class ShowHotkey
{
    private const int WmHotkey = 0x0312;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkY = 0x59;
    private const int HotkeyId = 1;

    private static HotkeyWindow? _window;

    public static void Start()
    {
        if (_window != null)
            return;
        _window = new HotkeyWindow();
        if (!RegisterHotKey(_window.Handle, HotkeyId, ModControl | ModShift | ModNoRepeat, VkY))
            AppLog.Warn("Ctrl+Shift+Y 단축키를 등록하지 못했습니다.");
    }

    public static void Stop()
    {
        if (_window == null)
            return;
        UnregisterHotKey(_window.Handle, HotkeyId);
        _window.DestroyHandle();
        _window = null;
    }

    private sealed class HotkeyWindow : NativeWindow
    {
        public HotkeyWindow()
        {
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId)
                TrayApplicationService.Instance.TogglePrimaryWindow();
            base.WndProc(ref m);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
