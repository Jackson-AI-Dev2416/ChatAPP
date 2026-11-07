using System.Windows;
using System.Windows.Controls;

namespace Ts3Messenger.Windows;

public partial class MultiComposeWindow : Window
{
    private bool _busy;
    private bool _fileDropInProgress;

    public event Func<string, Task>? SendTextAsync;
    public event Func<Task>? SendFilesAsync;
    public event Func<IReadOnlyList<string>, Task>? SendDroppedFilesAsync;
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

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_busy && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_busy || _fileDropInProgress || SendDroppedFilesAsync == null)
            return;
        if (!TryGetDroppedPaths(e.Data, out var paths))
            return;

        _fileDropInProgress = true;
        try
        {
            await SendDroppedFilesAsync(paths);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
        finally
        {
            _fileDropInProgress = false;
        }
    }

    private static bool TryGetDroppedPaths(IDataObject data, out string[] paths)
    {
        paths = Array.Empty<string>();
        if (!data.GetDataPresent(DataFormats.FileDrop))
            return false;
        var rawObj = data.GetData(DataFormats.FileDrop, autoConvert: true);
        if (rawObj is not string[] raw || raw.Length == 0)
            return false;
        paths = raw.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        return paths.Length > 0;
    }
}
