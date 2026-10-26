using System.Windows;
using System.Windows.Controls;

namespace Ts3Messenger.Windows;

public partial class MultiComposeWindow : Window
{
    private bool _busy;

    public event Func<string, Task>? SendTextAsync;
    public event Func<Task>? SendFilesAsync;
    public event Func<Task>? SendSnapshotAsync;

    public MultiComposeWindow(string targetSummary)
    {
        InitializeComponent();
        TargetText.Text = targetSummary;
    }

    public void SetBusy(bool busy, string? status)
    {
        _busy = busy;
        SnapshotButton.IsEnabled = !busy;
        FileButton.IsEnabled = !busy;
        BodyBox.IsEnabled = !busy;
        SendButton.IsEnabled = !busy && BodyBox.Text.Trim().Length > 0;
        StatusText.Text = status ?? "";
    }

    private void OnBodyChanged(object sender, TextChangedEventArgs e)
    {
        if (!_busy)
            SendButton.IsEnabled = BodyBox.Text.Trim().Length > 0;
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        var text = BodyBox.Text.Trim();
        if (text.Length == 0 || SendTextAsync == null)
            return;
        try
        {
            await SendTextAsync(text);
            BodyBox.Clear();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void OnFiles(object sender, RoutedEventArgs e)
    {
        if (SendFilesAsync == null)
            return;
        try
        {
            await SendFilesAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void OnSnapshot(object sender, RoutedEventArgs e)
    {
        if (SendSnapshotAsync == null)
            return;
        try
        {
            await SendSnapshotAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }
}
