using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.WindowsAPICodePack.Controls;
using Microsoft.WindowsAPICodePack.Controls.WindowsPresentationFoundation;
using Microsoft.WindowsAPICodePack.Shell;
using Ts3Messenger.Services;

namespace Ts3Messenger.Windows;

public partial class FileFolderPickerWindow : Window
{
    private bool _syncingFileName;
    private int _syncGeneration;
    private DispatcherTimer? _selectionRetryTimer;

    public IReadOnlyList<string> SelectedPaths { get; private set; } = Array.Empty<string>();

    public FileFolderPickerWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var browser = ExplorerBrowserHost.ExplorerBrowserControl;
            if (browser == null)
                return;

            browser.ContentOptions.Flags &= ~ExplorerBrowserContentSectionOptions.SingleSelection;
            if (KnownFolders.Computer is ShellObject computerRoot)
                browser.Navigate(computerRoot);
            else
                browser.Navigate(ShellObject.FromParsingName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
            browser.SelectionChanged += (_, _) => ScheduleSyncFileNameFromSelection();
        }, DispatcherPriority.Loaded);
    }

    private void ScheduleSyncFileNameFromSelection()
    {
        var generation = ++_syncGeneration;
        Dispatcher.BeginInvoke(
            () => SyncFileNameFromSelection(generation, force: false),
            DispatcherPriority.Input);
        Dispatcher.BeginInvoke(
            () => SyncFileNameFromSelection(generation, force: false),
            DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(
            () => SyncFileNameFromSelection(generation, force: false),
            DispatcherPriority.ApplicationIdle);

        _selectionRetryTimer?.Stop();
        _selectionRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        var attempts = 0;
        _selectionRetryTimer.Tick += (_, _) =>
        {
            attempts++;
            SyncFileNameFromSelection(generation, force: false);
            if (attempts >= 12 || !string.IsNullOrWhiteSpace(FileNameBox.Text))
                _selectionRetryTimer.Stop();
        };
        _selectionRetryTimer.Start();
    }

    private void SyncFileNameFromSelection(int generation, bool force)
    {
        if (generation != _syncGeneration || _syncingFileName)
            return;

        var names = new List<string>();
        foreach (ShellObject item in EnumerateSelectedItems())
        {
            var label = GetSelectionLabel(item);
            if (!string.IsNullOrWhiteSpace(label))
                names.Add(label);
        }

        var text = names.Count == 0 ? string.Empty : string.Join("; ", names);
        if (!force && FileNameBox.Text == text)
            return;

        _syncingFileName = true;
        FileNameBox.Text = text;
        FileNameBox.CaretIndex = text.Length;
        _syncingFileName = false;
    }

    private IEnumerable<ShellObject> EnumerateSelectedItems()
    {
        var inner = ExplorerBrowserHost.ExplorerBrowserControl?.SelectedItems;
        if (inner is { Count: > 0 })
        {
            foreach (ShellObject item in inner)
                yield return item;
            yield break;
        }

        foreach (ShellObject item in ExplorerBrowserHost.SelectedItems)
            yield return item;
    }

    private static string? GetSelectionLabel(ShellObject item)
    {
        if (!string.IsNullOrWhiteSpace(item.Name))
            return item.Name;

        var path = ShellItemPath.TryGetFileSystemPath(item);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (Directory.Exists(path))
            return new DirectoryInfo(path).Name;

        return Path.GetFileName(path);
    }

    private void OnSelectClick(object sender, RoutedEventArgs e)
    {
        SyncFileNameFromSelection(_syncGeneration, force: true);
        var paths = CollectPaths();
        if (paths.Count == 0)
        {
            AppDialog.Show("Select a file or folder to send.", Title,
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SelectedPaths = paths;
        DialogResult = true;
        Close();
    }

    private List<string> CollectPaths()
    {
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var currentDirectory = TryGetCurrentDirectoryPath();

        foreach (var item in EnumerateSelectedItems())
        {
            var path = ShellItemPath.TryGetFileSystemPath(item);
            if (path != null && seen.Add(path))
                paths.Add(path);
        }

        if (paths.Count > 0)
            return paths;

        foreach (var raw in ParseManualPaths(FileNameBox.Text, currentDirectory))
        {
            if (seen.Add(raw))
                paths.Add(raw);
        }

        return paths;
    }

    private string? TryGetCurrentDirectoryPath()
    {
        var location = ExplorerBrowserHost.ExplorerBrowserControl?.NavigationLog.CurrentLocation;
        return location == null ? null : ShellItemPath.TryGetFileSystemPath(location);
    }

    private static IEnumerable<string> ParseManualPaths(string text, string? currentDirectory)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;

        foreach (var segment in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = segment.Trim('"');
            var resolved = ResolveExistingPath(path, currentDirectory);
            if (resolved != null)
                yield return resolved;
        }
    }

    private static string? ResolveExistingPath(string path, string? currentDirectory)
    {
        if (Directory.Exists(path) || File.Exists(path))
            return Path.GetFullPath(path);

        if (string.IsNullOrWhiteSpace(currentDirectory))
            return null;

        var combined = Path.Combine(currentDirectory, path);
        if (Directory.Exists(combined) || File.Exists(combined))
            return Path.GetFullPath(combined);

        return null;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
