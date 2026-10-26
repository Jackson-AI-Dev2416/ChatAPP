using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using System.Text.Json;
using Microsoft.Win32;
using Ts3Messenger.Models;
using Ts3Messenger.Services;
using Ts3Messenger.Windows;
using Run = System.Windows.Documents.Run;
using TextElement = System.Windows.Documents.TextElement;

namespace Ts3Messenger;

public partial class MainWindow : Window
{
    private readonly Ts3ApiClient _api;
    private readonly UserMe _me;
    private readonly MessageCryptoService _crypto;
    private readonly KeyStore _keys;
    private readonly ChatWebSocketClient _ws = new();
    private readonly FileTransferTcpService _fileTcp = new();
    private readonly FileReceiveCoordinator _fileReceiveCoordinator = new();
    private readonly ObservableCollection<ContactVm> _contacts = new();
    private readonly List<ChatMessage> _messages = new();
    private ContactVm? _selected;
    private string? _currentRoomId;
    private string? _currentPeerId;
    private readonly string _serverBaseUrl;
    private bool _contactSwitchInProgress;
    private readonly Dictionary<string, string> _senderFilePaths = new();
    private readonly Dictionary<string, FileSendPayload> _ephemeralSendPayloads = new();
    private readonly Dictionary<string, string> _recipientSavePaths = new();
    private readonly Dictionary<string, string> _transferLocalPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonElement> _pendingFilePunches = new();
    private readonly HashSet<string> _senderTransferStarted = new();
    private readonly HashSet<string> _subscribedRooms = new(StringComparer.OrdinalIgnoreCase);
    private bool _allowRealClose;
    private bool _keepMessengerOpen;
    private bool _silentTrayStartup;
    private bool _pendingServerRecovery;
    private bool _showMainWhenRestored;
    private bool _fileDropInProgress;
    private readonly HashSet<string> _selectedMessageIds = new(StringComparer.OrdinalIgnoreCase);
    private string? _selectionAnchorMessageId;
    private bool _messageSelectionMode;
    private DispatcherTimer? _longPressTimer;
    private ChatMessage? _longPressMessage;
    private bool _longPressActivated;
    private Point _longPressStart;
    private ChatMessage? _replyTo;
    private ChatMessage? _editingMessage;
    private int _contactsRefreshGeneration;
    private readonly Dictionary<string, int> _punchWatchGeneration = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _punchWatchRooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _transferRoomIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _senderTransferCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _recipientReceiveCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _userCancelledTransfers = new(StringComparer.OrdinalIgnoreCase);
    private readonly TransferStateStore _transferStore = new();
    private readonly HashSet<string> _deliveredMarked = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _transferSizes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _transferRecipients = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _transferViewerStatus = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Host, int Port)> _senderListenEndpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _notifiedTransfers = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow(string serverBaseUrl, Ts3ApiClient api, UserMe me, MessageCryptoService crypto, KeyStore keys)
    {
        InitializeComponent();
        AppIcon.ApplyToWindow(this);
        _api = api;
        _me = me;
        _crypto = crypto;
        _keys = keys;
        _serverBaseUrl = serverBaseUrl;
        Title = $"TS3 Messenger — {me.login_id}";
        MyIdText.Text = me.login_id;
        MyIpText.Text = FileTransferTcpService.PickLanAddress();
        MyAvatarText.Text = AvatarPalette.Initial(me.login_id);
        MyAvatar.Background = AvatarPalette.For(me.id);
        ContactList.ItemsSource = _contacts;
        CollectionViewSource.GetDefaultView(_contacts).Filter = MatchesContactSearch;
        UpdateThemeGlyph();
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;
        ComposeBox.TextChanged += (_, _) => UpdateComposePlaceholder();
        ComposeBox.Document.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
        DataObject.AddPastingHandler(ComposeBox, OnComposePasting);
        SourceInitialized += OnSourceInitialized;
        Loaded += OnMainWindowLoaded;
        Activated += (_, _) => _ = MarkCurrentRoomReadIfViewingAsync();
        IsVisibleChanged += (_, _) =>
        {
            UpdateTrayAttention();
            if (!IsVisible)
                return;
            _ = MarkCurrentRoomReadIfViewingAsync();
            _ = Dispatcher.InvokeAsync(async () => await RecoverRoomTransfersAsync());
        };
        StateChanged += (_, _) =>
        {
            UpdateTrayAttention();
            _ = MarkCurrentRoomReadIfViewingAsync();
        };
        Closing += OnWindowClosing;
        Closed += OnMainWindowClosed;
        _fileTcp.Progress += OnFileProgress;
        EmojiPickerControl.EmojiSelected += OnEmojiPicked;
        PreviewMouseDown += OnWindowPreviewMouseDown;
        Deactivated += (_, _) => EmojiPopup.IsOpen = false;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        ComposeBox.PreviewKeyDown += OnComposePreviewKeyDown;
        ComposePanel.PreviewMouseDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src && !IsDescendantOf(ComposeBox, src))
                return;
            if (!ComposeBox.IsFocused)
                ComposeBox.Focus();
        };
        ComposeBox.FontSize = AppTypography.Compose;
        WireFileDropTarget(ComposePanel);
        WireFileDropTarget(ChatAreaBorder);
    }

    private void WireFileDropTarget(UIElement element)
    {
        element.AllowDrop = true;
        element.PreviewDragOver += OnFileDragOver;
        element.PreviewDrop += OnFileDrop;
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

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnFileDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_fileDropInProgress)
            return;
        if (!TryGetDroppedPaths(e.Data, out var paths))
            return;

        if (_currentRoomId == null)
        {
            AppDialog.Show("Select a chat before dropping files.", "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _fileDropInProgress = true;
        try
        {
            await PromptAndSendFilesFromPathsAsync(paths);
        }
        catch (Exception ex)
        {
            AppDialog.Show(UserErrorMessages.ForUi(ex, "file transfer"), "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _fileDropInProgress = false;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        if (!_messageSelectionMode && _selectedMessageIds.Count == 0)
            return;
        e.Handled = true;
        ClearMessageSelection();
    }

    private void OnComposePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsEnterKey(e))
            return;
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            e.Handled = true;
            System.Windows.Documents.EditingCommands.EnterLineBreak.Execute(null, ComposeBox);
            return;
        }

        e.Handled = true;
        _ = Dispatcher.BeginInvoke(SendCurrentAsync, DispatcherPriority.Input);
    }

    private static bool IsEnterKey(KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return)
            return true;
        if (e.Key == Key.ImeProcessed &&
            e.ImeProcessedKey is Key.Enter or Key.Return)
            return true;
        return e.Key == Key.System && e.SystemKey is Key.Enter or Key.Return;
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!EmojiPopup.IsOpen) return;
        if (IsDescendantOf(EmojiButton, e.OriginalSource as DependencyObject)) return;
        if (IsDescendantOf(EmojiPickerControl, e.OriginalSource as DependencyObject)) return;
        EmojiPopup.IsOpen = false;
    }

    private static bool IsDescendantOf(DependencyObject root, DependencyObject? node)
    {
        while (node != null)
        {
            if (node == root) return true;
            node = GetParentObject(node);
        }
        return false;
    }

    private static DependencyObject? GetParentObject(DependencyObject node)
    {
        if (node is Visual)
            return VisualTreeHelper.GetParent(node);
        if (node is FrameworkContentElement contentElement)
            return contentElement.Parent;
        return LogicalTreeHelper.GetParent(node);
    }

    public void EnableSilentTrayStartup() => _silentTrayStartup = true;

    public void PrepareHiddenTrayStart()
    {
        ShowActivated = false;
        ShowInTaskbar = false;
        Opacity = 0;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (!_silentTrayStartup)
            return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, SwHide);
    }

    private const int SwHide = 0;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public void ShowFromTray()
    {
        Opacity = 1;
        ShowInTaskbar = true;
        ShowActivated = true;
        if (!IsVisible)
            Show();
        Activate();
    }

    private async void OnMainWindowClosed(object? sender, EventArgs e)
    {
        var resumeAfterServerLoss = _pendingServerRecovery;
        var showWhenRestored = _showMainWhenRestored;
        _pendingServerRecovery = false;
        await _ws.DisposeAsync();
        if (!resumeAfterServerLoss || TrayApplicationService.Instance.ExitRequested)
            return;
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (TrayApplicationService.Instance.ExitRequested)
                return;
            if (showWhenRestored)
                SessionBootstrap.ShowLoginWindow(retryUntilServerReturns: true);
            else
                BackgroundSignInService.Instance.BeginServerRecovery(showMainWindowWhenRestored: false);
        });
    }

    private async void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_silentTrayStartup)
        {
            ShowInTaskbar = false;
            Opacity = 0;
            Hide();
        }

        try
        {
            await BootstrapAsync(_serverBaseUrl);
            _silentTrayStartup = false;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"메인 창 초기화 실패: {ex}");
            if (_silentTrayStartup)
            {
                _silentTrayStartup = false;
                PrepareForRealClose();
                Close();
                return;
            }

            AppDialog.Show(
                UserErrorMessages.ForUi(ex, "main window"),
                "TS3 Messenger",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task BootstrapAsync(string serverBaseUrl)
    {
        await ChatTimestamps.TryLoadFromServerAsync(_api);
        try
        {
            await _api.PutPublicKeyAsync(_crypto.ExportPublicKeyB64());
        }
        catch (Exception ex)
        {
            AppLog.Warn($"공개키 등록 실패: {ex.Message}");
        }

        var wsUri = new Uri(serverBaseUrl.Replace("https://", "wss://").Replace("http://", "ws://").TrimEnd('/') + "/api/v1/ws/chat");
        _ws.OnEvent += HandleWsEvent;
        _ws.OnReconnected += OnChatWebSocketReconnected;
        _ws.OnServerLost += OnServerLost;
        await _ws.ConnectAsync(wsUri, _api.CookieHeader);
        await RefreshContactsAsync();
    }

    private void OnChatWebSocketReconnected() =>
        _ = Dispatcher.InvokeAsync(async () =>
        {
            foreach (var roomId in _subscribedRooms.ToArray())
                await _ws.SubscribeAsync(roomId);
            await RefreshContactsAsync();
            if (_currentRoomId != null)
                await RecoverRoomTransfersAsync();
        });

    private async Task RefreshContactsAsync()
    {
        var generation = Interlocked.Increment(ref _contactsRefreshGeneration);
        List<ContactItem> rows;
        try
        {
            rows = await _api.ContactsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"주소록 갱신 실패: {ex.Message}");
            return;
        }

        if (generation != _contactsRefreshGeneration)
            return;

        List<RoomItem>? rooms = null;
        try
        {
            rooms = await _api.RoomsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"방 목록 실패: {ex.Message}");
        }

        var selectedPeerId = _selected?.Id;
        SyncContactsFromApi(rows);
        if (rooms != null)
            SyncGroups(rooms);

        if (!string.IsNullOrEmpty(selectedPeerId))
        {
            var normalized = ChatIds.NormalizeUserId(selectedPeerId);
            var selectedVm = _contacts.FirstOrDefault(c => ChatIds.NormalizeUserId(c.Id) == normalized);
            if (selectedVm != null)
            {
                _selected = selectedVm;
                ContactList.SelectedItem = selectedVm;
            }
        }

        await SubscribeToContactRoomsAsync(rows);
        await RecoverBackgroundRoomTransfersAsync(rows);
        if (rooms != null)
        {
            var groupRooms = rooms
                .Where(r => string.Equals(r.RoomType, "group", StringComparison.OrdinalIgnoreCase))
                .Select(r => new ContactItem { Id = r.Id, DirectRoomId = r.Id });
            await RecoverBackgroundRoomTransfersAsync(groupRooms);
        }
        UpdateTaskbarBadge();
    }

    private void SyncContactsFromApi(List<ContactItem> rows)
    {
        var incomingIds = new HashSet<string>(
            rows.Select(c => ChatIds.NormalizeUserId(c.Id)),
            StringComparer.OrdinalIgnoreCase);
        for (var i = _contacts.Count - 1; i >= 0; i--)
        {
            var id = ChatIds.NormalizeUserId(_contacts[i].Id);
            if (!incomingIds.Contains(id) && !_contacts[i].IsGroup)
                _contacts.RemoveAt(i);
        }

        foreach (var c in rows)
        {
            var norm = ChatIds.NormalizeUserId(c.Id);
            var vm = _contacts.FirstOrDefault(x => ChatIds.NormalizeUserId(x.Id) == norm);
            if (vm == null)
            {
                _contacts.Add(ContactVm.From(c));
                continue;
            }

            vm.ApplyFrom(c);
        }

        SortContactsByRecency();
    }

    private void SyncGroups(List<RoomItem> rooms)
    {
        var groups = rooms
            .Where(r => string.Equals(r.RoomType, "group", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var ids = new HashSet<string>(
            groups.Select(g => GroupContactId(g.Id)),
            StringComparer.OrdinalIgnoreCase);
        for (var i = _contacts.Count - 1; i >= 0; i--)
        {
            if (_contacts[i].IsGroup && !ids.Contains(_contacts[i].Id))
                _contacts.RemoveAt(i);
        }

        foreach (var room in groups)
        {
            var id = GroupContactId(room.Id);
            var vm = _contacts.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (vm == null)
            {
                vm = ContactVm.ForGroup(room);
                vm.LoginId = GroupDisplayName(room);
                vm.CanDeleteGroup = IsGroupCreator(room.CreatedByUserId);
                _contacts.Add(vm);
                continue;
            }

            vm.LoginId = GroupDisplayName(room);
            vm.DirectRoomId = ChatIds.NormalizeRoomId(room.Id);
            vm.CanDeleteGroup = IsGroupCreator(room.CreatedByUserId);
            vm.UnreadCount = room.UnreadCount;
            vm.SetPreview(room.LastMessagePreview, room.LastMessageAt, room.LastMessageMine);
        }

        SortContactsByRecency();
    }

    private static string GroupContactId(string roomId) => "group:" + ChatIds.NormalizeRoomId(roomId);

    private bool IsGroupCreator(string? userId) =>
        !string.IsNullOrEmpty(userId)
        && ChatIds.NormalizeUserId(userId) == ChatIds.NormalizeUserId(_me.id);

    private string GroupDisplayName(RoomItem room)
    {
        var name = string.IsNullOrWhiteSpace(room.Name) ? "Group" : room.Name.Trim();
        var creator = CreatorLoginId(room.CreatedByUserId);
        return string.IsNullOrEmpty(creator) ? name : $"{name} ({creator})";
    }

    private string CreatorLoginId(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return "";
        var norm = ChatIds.NormalizeUserId(userId);
        if (norm == ChatIds.NormalizeUserId(_me.id))
            return _me.login_id;
        var contact = _contacts.FirstOrDefault(c =>
            !c.IsGroup && ChatIds.NormalizeUserId(c.Id) == norm);
        return string.IsNullOrWhiteSpace(contact?.LoginId) ? "" : contact.LoginId;
    }

    private async Task DeleteCurrentGroupAsync()
    {
        if (_selected?.CanDeleteGroup != true || string.IsNullOrEmpty(_currentRoomId))
            return;
        if (AppDialog.Show(
                "Delete this group for every member?",
                "Delete group",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var roomId = _currentRoomId;
        try
        {
            await _api.DeleteGroupAsync(roomId);
        }
        catch (Exception ex)
        {
            AppDialog.Show(UserErrorMessages.ForUi(ex, "delete group"), "Delete group",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        RemoveGroupLocally(roomId);
    }

    private void RemoveGroupLocally(string roomId)
    {
        if (string.IsNullOrWhiteSpace(roomId))
            return;
        var id = GroupContactId(roomId);
        var vm = _contacts.FirstOrDefault(c => c.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (vm != null)
            _contacts.Remove(vm);
        if (ChatIds.NormalizeRoomId(_currentRoomId ?? "") == ChatIds.NormalizeRoomId(roomId))
            ShowEmptyChat();
    }

    private void ShowEmptyChat()
    {
        _selected = null;
        _currentRoomId = null;
        _currentPeerId = null;
        _messages.Clear();
        ContactList.SelectedItem = null;
        ChatHeader.Visibility = Visibility.Collapsed;
        ComposePanel.Visibility = Visibility.Collapsed;
        EmptyStatePanel.Visibility = Visibility.Visible;
        GroupMembersPopup.IsOpen = false;
        RenderMessages();
    }

    private async void OnGroupTitleClick(object sender, MouseButtonEventArgs e)
    {
        if (_selected?.IsGroup != true || string.IsNullOrEmpty(_currentRoomId))
            return;
        List<RoomMemberItem> members;
        try
        {
            members = await _api.RoomMembersAsync(_currentRoomId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"그룹 멤버 조회 실패: {ex.Message}");
            return;
        }

        GroupMembersPanel.Children.Clear();
        GroupMembersPanel.Children.Add(new TextBlock
        {
            Text = "Members",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
        });
        foreach (var member in members.OrderBy(m => m.LoginId, StringComparer.OrdinalIgnoreCase))
        {
            var norm = ChatIds.NormalizeUserId(member.UserId);
            var mine = norm == ChatIds.NormalizeUserId(_me.id);
            var contact = _contacts.FirstOrDefault(c => !c.IsGroup && ChatIds.NormalizeUserId(c.Id) == norm);
            var online = mine || contact?.Online == true;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (System.Windows.Media.Brush)FindResource(online ? "SuccessBrush" : "TextSubtleBrush"),
            });
            row.Children.Add(new TextBlock
            {
                Text = mine ? $"{member.LoginId} (you)" : member.LoginId,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            GroupMembersPanel.Children.Add(row);
        }

        GroupMembersPopup.IsOpen = true;
        e.Handled = true;
    }

    private bool _contactOrderChanging;

    private void SortContactsByRecency()
    {
        var ordered = _contacts
            .Select((contact, index) => (contact, index))
            .OrderBy(x => x.contact.PreviewAt.HasValue ? 0 : 1)
            .ThenByDescending(x => x.contact.PreviewAt ?? DateTime.MinValue)
            .ThenBy(x => x.contact.LoginId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.index)
            .Select(x => x.contact)
            .ToList();
        for (var target = 0; target < ordered.Count; target++)
        {
            var current = _contacts.IndexOf(ordered[target]);
            if (current >= 0 && current != target)
                MoveContact(current, target);
        }
    }

    private void BringContactToTop(ContactVm contact)
    {
        var index = _contacts.IndexOf(contact);
        if (index > 0)
            MoveContact(index, 0);
    }

    private void MoveContact(int from, int to)
    {
        _contactOrderChanging = true;
        try
        {
            _contacts.Move(from, to);
        }
        finally
        {
            _contactOrderChanging = false;
        }
    }

    private async Task SubscribeToContactRoomsAsync(IEnumerable<ContactItem> rows)
    {
        foreach (var c in rows)
        {
            if (string.IsNullOrWhiteSpace(c.DirectRoomId))
                continue;
            var roomId = ChatIds.NormalizeRoomId(c.DirectRoomId);
            if (!_subscribedRooms.Add(roomId))
                continue;
            await _ws.SubscribeAsync(roomId);
        }
    }

    public async Task TryOpenPendingPeerFromNotificationAsync()
    {
        var peerId = TrayApplicationService.Instance.TakePendingPeerId();
        if (string.IsNullOrEmpty(peerId))
            return;
        await OpenPeerChatAsync(peerId);
    }

    private async Task OpenPeerChatAsync(string peerId)
    {
        var normalized = ChatIds.NormalizeUserId(peerId);
        var vm = _contacts.FirstOrDefault(c => ChatIds.NormalizeUserId(c.Id) == normalized);
        if (vm == null)
        {
            await RefreshContactsAsync();
            vm = _contacts.FirstOrDefault(c => ChatIds.NormalizeUserId(c.Id) == normalized);
        }

        if (vm == null)
            return;

        if (!MatchesContactSearch(vm))
            ContactSearchBox.Text = "";
        ContactList.SelectedItem = vm;
    }

    private bool _contactFilterRefreshing;

    private bool MatchesContactSearch(object item)
    {
        var query = ContactSearchBox?.Text.Trim() ?? "";
        return query.Length == 0
               || item is ContactVm c && c.LoginId.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OnSearchFieldClick(object sender, MouseButtonEventArgs e)
    {
        if (!ContactSearchBox.IsKeyboardFocused)
            ContactSearchBox.Focus();
    }

    private void OnChatListMenu(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = ChatListMenuButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var group = new MenuItem { Header = "New Group" };
        group.Click += async (_, _) => await CreateGroupAsync();
        var multi = new MenuItem { Header = "Multi message" };
        multi.Click += (_, _) => StartMultiMessage();
        menu.Items.Add(group);
        menu.Items.Add(multi);
        menu.IsOpen = true;
    }

    private List<PickPerson> AddressBookPeople() =>
        _contacts.Where(c => !c.IsGroup)
            .Select(c => new PickPerson { Id = c.Id, LoginId = c.LoginId })
            .ToList();

    private async Task CreateGroupAsync()
    {
        var nameWindow = new GroupNameWindow { Owner = this };
        if (nameWindow.ShowDialog() != true)
            return;
        var pick = new PeerPickWindow(AddressBookPeople(), "Add members", "Create") { Owner = this };
        if (pick.ShowDialog() != true)
            return;
        try
        {
            var created = await _api.CreateGroupAsync(nameWindow.GroupName, pick.SelectedIds);
            await RefreshContactsAsync();
            var vm = _contacts.FirstOrDefault(c => c.Id.Equals(GroupContactId(created.Id), StringComparison.OrdinalIgnoreCase));
            if (vm != null)
                ContactList.SelectedItem = vm;
        }
        catch (Exception ex)
        {
            AppDialog.Show(UserErrorMessages.ForUi(ex, "create group"), "New Group",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void StartMultiMessage()
    {
        var pick = new PeerPickWindow(AddressBookPeople(), "Multi message", "Select") { Owner = this };
        if (pick.ShowDialog() != true)
            return;
        var ids = pick.SelectedIds.ToList();
        var names = ids.Select(LookupContactLoginId).ToList();
        var summary = names.Count <= 4
            ? string.Join(", ", names)
            : string.Join(", ", names.Take(4)) + $" and {names.Count - 4} more";
        var compose = new MultiComposeWindow($"To {ids.Count}: {summary}");
        compose.SendTextAsync += async text =>
        {
            compose.SetBusy(true, "Sending...");
            var status = await FanOutTextAsync(ids, text);
            compose.SetBusy(false, status);
        };
        compose.SendFilesAsync += async () =>
        {
            var picker = new Windows.FileFolderPickerWindow { Owner = compose };
            if (picker.ShowDialog() != true || picker.SelectedPaths.Count == 0)
                return;
            OutgoingBundle bundle;
            try
            {
                bundle = await FileSendPlanner.FromPathsAsync(picker.SelectedPaths);
            }
            catch (FileSendPlanException ex)
            {
                AppDialog.Show(ex.Message, "Multi message", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (bundle.TotalSize > FileTransferLimits.MaxFileSizeBytes)
            {
                AppDialog.Show($"Too large to send (max {FileTransferLimits.MaxFileSizeLabel} in total).",
                    "Multi message", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var confirm = new FileSendConfirmWindow(bundle) { Owner = compose };
            if (confirm.ShowDialog() != true)
                return;
            compose.SetBusy(true, "Sending files...");
            var status = await FanOutFilesAsync(ids, bundle, confirm.Caption);
            compose.SetBusy(false, status);
        };
        compose.SendSnapshotAsync += async () =>
        {
            compose.Hide();
            var path = await CaptureSnapshotFileAsync();
            compose.Show();
            compose.Activate();
            if (string.IsNullOrEmpty(path))
                return;
            compose.SetBusy(true, "Sending snapshot...");
            var status = await FanOutSnapshotAsync(ids, path);
            compose.SetBusy(false, status);
        };
        compose.Owner = this;
        compose.ShowInTaskbar = false;
        compose.Closing += (_, _) => _keepMessengerOpen = true;
        compose.Closed += (_, _) =>
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _keepMessengerOpen = false;
                if (!IsVisible)
                    Show();
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Activate();
            }));
        };
        compose.Show();
    }

    private async Task<string> EnsureDirectRoomAsync(string userId)
    {
        var norm = ChatIds.NormalizeUserId(userId);
        var contact = _contacts.FirstOrDefault(c => !c.IsGroup && ChatIds.NormalizeUserId(c.Id) == norm);
        if (!string.IsNullOrEmpty(contact?.DirectRoomId))
        {
            var existing = ChatIds.NormalizeRoomId(contact.DirectRoomId);
            await EnsureRoomSubscribedAsync(existing);
            return existing;
        }

        var room = ChatIds.NormalizeRoomId(await _api.CreateDirectRoomAsync(norm));
        if (contact != null)
            contact.DirectRoomId = room;
        await EnsureRoomSubscribedAsync(room);
        return room;
    }

    private async Task<string> FanOutTextAsync(IReadOnlyList<string> userIds, string text)
    {
        var failed = new List<string>();
        foreach (var id in userIds)
        {
            try
            {
                var room = await EnsureDirectRoomAsync(id);
                var saved = await _api.PostMessageAsync(room, text, Guid.NewGuid().ToString("N"));
                var at = ChatTimestamps.NormalizeUtc(saved.sent_at);
                if (ChatIds.NormalizeRoomId(_currentRoomId ?? "") == room)
                    AddMessageIfNew(saved, text, true);
                else
                    SetLocalContactPreview(room, text, at, true);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"여러 명 메시지 실패 ({id}): {ex.Message}");
                failed.Add(LookupContactLoginId(id));
            }
        }

        return failed.Count == 0
            ? $"Sent to {userIds.Count}."
            : "Could not send to " + string.Join(", ", failed);
    }

    private async Task<string> FanOutFilesAsync(IReadOnlyList<string> userIds, OutgoingBundle bundle, string? caption)
    {
        var failed = new List<string>();
        foreach (var id in userIds)
        {
            try
            {
                var room = await EnsureDirectRoomAsync(id);
                await SendBundleAsync(bundle, caption, room);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"여러 명 파일 실패 ({id}): {ex.Message}");
                failed.Add(LookupContactLoginId(id));
            }
        }

        return failed.Count == 0
            ? $"Sent to {userIds.Count}."
            : "Could not send to " + string.Join(", ", failed);
    }

    private async Task<string> FanOutSnapshotAsync(IReadOnlyList<string> userIds, string path)
    {
        var failed = new List<string>();
        foreach (var id in userIds)
        {
            try
            {
                var room = await EnsureDirectRoomAsync(id);
                await SendSnapshotFileAsync(path, room);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"여러 명 스냅샷 실패 ({id}): {ex.Message}");
                failed.Add(LookupContactLoginId(id));
            }
        }

        return failed.Count == 0
            ? $"Sent to {userIds.Count}."
            : "Could not send to " + string.Join(", ", failed);
    }

    private void OnContactSearchChanged(object sender, TextChangedEventArgs e)
    {
        ContactSearchPlaceholder.Visibility = ContactSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _contactFilterRefreshing = true;
        try
        {
            CollectionViewSource.GetDefaultView(_contacts).Refresh();
            if (_selected != null && MatchesContactSearch(_selected))
                ContactList.SelectedItem = _selected;
        }
        finally
        {
            _contactFilterRefreshing = false;
        }
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e) => ThemeService.Toggle();

    private void OnThemeChanged()
    {
        UpdateThemeGlyph();
        RenderMessages();
    }

    private void UpdateThemeGlyph() =>
        ThemeToggleButton.Content = ThemeService.IsDark ? "\uE706" : "\uE708";

    private void UpdateComposePlaceholder() =>
        ComposePlaceholder.Visibility = ComposeInputHelper.IsEmpty(ComposeBox) ? Visibility.Visible : Visibility.Collapsed;

    private void OnChatMenuClick(object sender, RoutedEventArgs e)
    {
        if (_currentRoomId == null)
            return;
        var menu = new ContextMenu
        {
            PlacementTarget = ChatMenuButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        var copyAll = new MenuItem { Header = "Copy entire chat", IsEnabled = _messages.Count > 0 };
        copyAll.Click += (_, _) => CopyMessagesToClipboard(_messages);
        menu.Items.Add(copyAll);
        var select = new MenuItem { Header = "Select messages", IsEnabled = _messages.Count > 0 };
        select.Click += (_, _) =>
        {
            _messageSelectionMode = true;
            UpdateMessageSelectionChrome();
        };
        menu.Items.Add(select);
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear chat" };
        clear.SetResourceReference(StyleProperty, "DangerMenuItemStyle");
        clear.Click += (_, _) => OnClearChat(clear, new RoutedEventArgs());
        menu.Items.Add(clear);
        if (_selected?.CanDeleteGroup == true)
        {
            var deleteGroup = new MenuItem { Header = "Delete group" };
            deleteGroup.SetResourceReference(StyleProperty, "DangerMenuItemStyle");
            deleteGroup.Click += async (_, _) => await DeleteCurrentGroupAsync();
            menu.Items.Add(deleteGroup);
        }
        menu.IsOpen = true;
    }

    private void UpdateTaskbarBadge()
    {
        var total = _contacts.ToArray().Sum(c => c.UnreadCount);
        TaskbarBadgeHelper.SetUnreadCount(this, total);
        UpdateTrayAttention();
    }

    private void UpdateTrayAttention()
    {
        var total = _contacts.ToArray().Sum(c => c.UnreadCount);
        var tuckedAway = !IsVisible || WindowState == WindowState.Minimized;
        TrayApplicationService.Instance.SetUnreadAttention(tuckedAway && total > 0);
    }

    private void ZeroUnreadBadgeForPeer(string peerUserId)
    {
        var normalized = ChatIds.NormalizeUserId(peerUserId);
        var contact = _contacts.FirstOrDefault(c => ChatIds.NormalizeUserId(c.Id) == normalized);
        if (contact == null || contact.UnreadCount == 0)
            return;
        contact.UnreadCount = 0;
        UpdateTaskbarBadge();
    }

    private async void OnContactSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_contactSwitchInProgress || _contactFilterRefreshing) return;
        if (_contactOrderChanging) return;
        if (ContactList.SelectedItem is not ContactVm vm) return;
        _contactSwitchInProgress = true;
        try
        {
            _selected = vm;
            ChatTitle.Text = vm.LoginId;
            ChatTitleHost.Cursor = vm.IsGroup ? Cursors.Hand : Cursors.Arrow;
            ChatHeader.DataContext = vm;
            ChatHeader.Visibility = Visibility.Visible;
            ComposePanel.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            _forceScrollToEnd = true;
            ClearMessageSelection();
            ClearReply();
            _currentPeerId = vm.Id;
            _currentRoomId = string.IsNullOrEmpty(vm.DirectRoomId)
                ? null
                : ChatIds.NormalizeRoomId(vm.DirectRoomId);
            if (string.IsNullOrEmpty(_currentRoomId))
            {
                if (vm.IsGroup)
                    return;
                _currentRoomId = ChatIds.NormalizeRoomId(await _api.CreateDirectRoomAsync(vm.Id));
                vm.DirectRoomId = _currentRoomId;
            }
            await EnsureRoomSubscribedAsync(_currentRoomId!);
            await LoadMessagesAsync();
            await RefreshContactsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("연락처 선택", ex);
            AppDialog.Show(
                UserErrorMessages.ForUi(ex, "open chat"),
                "TS3 Messenger",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _contactSwitchInProgress = false;
        }
    }

    private async void OnContactRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        var item = list.InputHitTest(e.GetPosition(list)) as DependencyObject;
        while (item != null && item is not ListBoxItem)
            item = GetParentObject(item);
        if (item is not ListBoxItem { DataContext: ContactVm vm }) return;
        list.SelectedItem = vm;
        if (AppDialog.Show(
                "Delete this chat history from your screen? It will stay for everyone else.",
                "Clear chat",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await ClearCurrentRoomHistoryAsync();
    }

    private int _serverLostHandled;

    private void OnServerLost()
    {
        if (Interlocked.Exchange(ref _serverLostHandled, 1) == 1)
            return;
        Dispatcher.BeginInvoke(SignOutBecauseServerDown);
    }

    private void SignOutBecauseServerDown()
    {
        _showMainWhenRestored = IsVisible;
        _pendingServerRecovery = true;
        SessionCookieStore.Clear();
        _api.Dispose();
        AppLog.Info("서버 연결 끊김 — 창을 닫은 뒤 10초 간격 자동 재로그인");
        PrepareForRealClose();
        Close();
    }

    private async void OnLogout(object sender, RoutedEventArgs e)
    {
        if (AppDialog.Show(
                "Log out now?",
                "TS3 Messenger",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            await _api.LogoutAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"로그아웃 API 실패: {ex.Message}");
            SessionCookieStore.Clear();
        }
        await _ws.DisposeAsync();
        _api.Dispose();
        BackgroundSignInService.Instance.StopRecovery();
        SessionBootstrap.ShowLoginWindow();
        PrepareForRealClose();
        Close();
    }

    public void PrepareForRealClose() => _allowRealClose = true;

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_allowRealClose || TrayApplicationService.Instance.ExitRequested)
            return;
        if (_keepMessengerOpen)
        {
            e.Cancel = true;
            return;
        }
        e.Cancel = true;
        TrayApplicationService.Instance.TryHideWindowOnUserClose(this);
    }

    private async void OnClearChat(object sender, RoutedEventArgs e)
    {
        if (_currentRoomId == null) return;
        if (AppDialog.Show(
                "Delete this chat history from your screen? It will stay for everyone else.",
                "Clear chat",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await ClearCurrentRoomHistoryAsync();
    }

    private async Task ClearCurrentRoomHistoryAsync()
    {
        if (string.IsNullOrEmpty(_currentRoomId)) return;
        try
        {
            await _api.ClearClientHistoryAsync(_currentRoomId, _ws);
            _messages.Clear();
            _senderFilePaths.Clear();
            _recipientSavePaths.Clear();
            _senderTransferStarted.Clear();
            _selected?.SetPreview(null, null, false);
            SortContactsByRecency();
            RenderMessages();
        }
        catch (Exception ex)
        {
            AppDialog.Show(
                UserErrorMessages.ForUi(ex, "Clear chat"),
                "TS3 Messenger",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task LoadMessagesAsync()
    {
        if (_currentRoomId == null) return;
        _messages.Clear();
        ClearMessageSelection();
        var rows = await _api.LoadMessagesAsync(_currentRoomId, _me.id);
        _messages.AddRange(rows);
        await EnsureRoomSubscribedAsync(_currentRoomId);
        await MergeRoomFileTransfersAsync(_currentRoomId);
        await AutoAcceptPendingSnapshotsInRoomAsync();
        await RecoverRoomTransfersAsync();
        RenderMessages();
        await MarkCurrentRoomReadIfViewingAsync();
    }

    private async Task EnsureRoomSubscribedAsync(string roomId)
    {
        var norm = ChatIds.NormalizeRoomId(roomId);
        if (!_subscribedRooms.Add(norm))
            return;
        await _ws.SubscribeAsync(norm);
    }

    private async Task RecoverRoomTransfersAsync()
    {
        if (_currentRoomId == null)
            return;

        foreach (var m in _messages.Where(x => x.IsFileTransfer && x.FileTransferId != null).ToList())
        {
            if (m.IsMine && m.FileStatus is "accepted" or "in_progress")
                await TryResumeOutgoingTransferAsync(m.FileTransferId!, m.FileName);
            else if (!m.IsMine && m.IsSnapshot && m.FileStatus is "accepted" or "in_progress")
                await EnsureIncomingSnapshotReceivingAsync(m);
            else if (!m.IsMine && m.FileStatus is "accepted" or "in_progress")
                await EnsureIncomingBundleReceivingAsync(m.FileTransferId!, m.RoomId);
        }
    }

    /// <summary>
    /// Runs on login and on every presence change, so a transfer continues as soon as
    /// the other side comes back online. Transfers never expire.
    /// </summary>
    private async Task RecoverBackgroundRoomTransfersAsync(IEnumerable<ContactItem> contacts)
    {
        var normMe = ChatIds.NormalizeUserId(_me.id);
        foreach (var c in contacts)
        {
            if (string.IsNullOrWhiteSpace(c.DirectRoomId))
                continue;
            var roomId = ChatIds.NormalizeRoomId(c.DirectRoomId);
            await EnsureRoomSubscribedAsync(roomId);
            List<JsonElement> transfers;
            try
            {
                transfers = await _api.LoadRoomFileTransfersAsync(roomId);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"백그라운드 파일 복구 목록 실패 ({roomId}): {ex.Message}");
                continue;
            }

            foreach (var raw in transfers)
            {
                var t = TransferJson.Parse(raw, roomId);
                var isMine = t.SenderUserId == normMe;
                var row = TransferJson.RowForViewer(t, _me.id, isMine);
                RememberTransferMeta(t);
                var status = ViewerStatus(t, row);
                if (status is "expired" or "cancelled")
                    continue;
                if (SnapshotTransferHelper.IsSnapshotFileName(t.FileName))
                {
                    var localPath = ResolveStoredImagePath(t.Id, t.FileName, status);
                    status = FileTransferViewStatus.WithLocalFileOverride(status, localPath);
                    if (!isMine && status == "pending")
                        await AcceptIncomingFileTransferCoreAsync(roomId, t.Id, t.FileName, true);
                    else if (!isMine && status is "accepted" or "in_progress")
                        await EnsureIncomingSnapshotReceivingByIdAsync(roomId, t.Id, t.FileName);
                    else if (isMine && status is "accepted" or "in_progress")
                        await TryResumeOutgoingTransferAsync(t.Id, t.FileName);
                    continue;
                }

                if (!isMine && status is "accepted" or "in_progress")
                    await EnsureIncomingBundleReceivingAsync(t.Id, roomId);
                else if (isMine && status is "accepted" or "in_progress")
                    await TryResumeOutgoingTransferAsync(t.Id, t.FileName);
            }
        }

        if (_messages.Any(m => m.IsFileTransfer && !m.IsMine && m.FileStatus is "accepted" or "in_progress"))
            RenderMessages();
    }

    private async Task EnsureIncomingBundleReceivingAsync(string transferId, string roomId)
    {
        if (_transferStore.GetReceiver(transferId) == null || _recipientReceiveCts.ContainsKey(transferId))
            return;
        var normRoom = ChatIds.NormalizeRoomId(roomId);
        _punchWatchRooms[transferId] = normRoom;
        await _ws.RequestFilePunchAsync(normRoom, transferId);
        SchedulePunchWatch(transferId, normRoom);
    }

    private void RememberTransferMeta(RoomFileTransfer t)
    {
        if (string.IsNullOrEmpty(t.Id))
            return;
        if (!string.IsNullOrEmpty(t.RoomId))
            _transferRoomIds[t.Id] = t.RoomId;
        _transferSizes[t.Id] = t.FileSizeBytes;
        _transferRecipients[t.Id] = t.Recipients.Select(r => r.UserId).ToList();
        var isMine = t.SenderUserId == ChatIds.NormalizeUserId(_me.id);
        _transferViewerStatus[t.Id] = ViewerStatus(t, TransferJson.RowForViewer(t, _me.id, isMine));
    }

    private static string ViewerStatus(RoomFileTransfer t, TransferRecipientRow? row) =>
        string.IsNullOrEmpty(row?.Status) ? t.Status : row.Status;

    private async Task MarkTransferDeliveredIfNeededAsync(string transferId, TransferRecipientRow? row)
    {
        if (row == null || row.Delivered || !_deliveredMarked.Add(transferId))
            return;
        try
        {
            await _api.MarkFileTransferDeliveredAsync(transferId);
        }
        catch (Exception ex)
        {
            _deliveredMarked.Remove(transferId);
            AppLog.Warn($"파일 수신 확인 실패 ({transferId}): {ex.Message}");
        }
    }

    private bool IsSenderOnline(ChatMessage m)
    {
        var sender = ChatIds.NormalizeUserId(m.SenderUserId);
        var contact = _contacts.FirstOrDefault(c => ChatIds.NormalizeUserId(c.Id) == sender);
        return contact?.Online ?? true;
    }

    private async Task EnsureIncomingSnapshotReceivingByIdAsync(string roomId, string transferId, string fileName)
    {
        var savePath = SnapshotPaths.ReceivedFilePath(transferId, fileName);
        _recipientSavePaths[transferId] = savePath;
        _punchWatchRooms[transferId] = ChatIds.NormalizeRoomId(roomId);
        await _ws.RequestFilePunchAsync(roomId, transferId);
        SchedulePunchWatch(transferId, roomId);
    }

    private async Task TryResumeOutgoingTransferAsync(string transferId, string? fileName = null)
    {
        var status = _messages.FirstOrDefault(m => m.FileTransferId == transferId)?.FileStatus
                     ?? _transferViewerStatus.GetValueOrDefault(transferId);
        if (status != null && status is not ("accepted" or "in_progress"))
            return;
        await StartSenderTransferAsync(transferId, fileName);
    }

    /// <summary>Local files for an outgoing transfer, or null when this PC is not the one that sent it.</summary>
    private (List<string> Paths, List<long> Sizes)? ResolveSenderBundle(string transferId, string? fileName)
    {
        var state = _transferStore.GetSender(transferId);
        if (state != null && state.LocalPaths.Count > 0 && state.LocalPaths.Count == state.Sizes.Count)
            return (state.LocalPaths, state.Sizes);
        var path = ResolveSenderFilePath(transferId, fileName);
        if (path == null)
            return null;
        return ([path], [new FileInfo(path).Length]);
    }

    private string? ResolveSenderFilePath(string transferId, string? fileName = null)
    {
        if (_senderFilePaths.TryGetValue(transferId, out var senderPath) && File.Exists(senderPath))
            return senderPath;
        var msg = _messages.FirstOrDefault(m => m.FileTransferId == transferId);
        if (msg != null && !string.IsNullOrEmpty(msg.LocalImagePath) && File.Exists(msg.LocalImagePath))
            return msg.LocalImagePath;
        if (_transferLocalPaths.TryGetValue(transferId, out var known) && File.Exists(known))
            return known;
        var snapshotName = msg?.FileName ?? fileName;
        if (!string.IsNullOrEmpty(snapshotName) && SnapshotTransferHelper.IsSnapshotFileName(snapshotName))
        {
            var persisted = SnapshotPaths.ReceivedFilePath(transferId, snapshotName);
            if (File.Exists(persisted))
                return persisted;
        }

        return SnapshotPaths.TryFindPersistedPath(transferId);
    }

    private async Task EnsureIncomingSnapshotReceivingAsync(ChatMessage m)
    {
        if (string.IsNullOrEmpty(m.FileTransferId) || _currentRoomId == null)
            return;
        var savePath = SnapshotPaths.ReceivedFilePath(m.FileTransferId, m.FileName);
        _recipientSavePaths[m.FileTransferId] = savePath;
        if (m.FileStatus == "pending")
        {
            await AcceptIncomingFileAsync(m, savePath);
            return;
        }

        var roomId = TransferRoomId(m.FileTransferId!) ?? _currentRoomId!;
        await _ws.RequestFilePunchAsync(roomId, m.FileTransferId);
        SchedulePunchWatch(m.FileTransferId, roomId);
    }

    private void SchedulePunchWatch(string transferId, string? roomId = null)
    {
        if (roomId != null)
            _punchWatchRooms[transferId] = ChatIds.NormalizeRoomId(roomId);
        var generation = _punchWatchGeneration.GetValueOrDefault(transferId) + 1;
        _punchWatchGeneration[transferId] = generation;
        _ = RunPunchWatchAsync(transferId, generation);
    }

    private async Task RunPunchWatchAsync(string transferId, int generation)
    {
        int[] delaysMs = [0, 2000, 5000, 10000, 20000, 30000];
        foreach (var delay in delaysMs)
        {
            if (delay > 0)
                await Task.Delay(delay);
            if (_punchWatchGeneration.GetValueOrDefault(transferId) != generation)
                return;
            if (!ShouldContinuePunchWatch(transferId))
                return;

            await Dispatcher.InvokeAsync(async () =>
            {
                if (_punchWatchGeneration.GetValueOrDefault(transferId) != generation)
                    return;
                if (!ShouldContinuePunchWatch(transferId))
                    return;
                var punchRoom = _punchWatchRooms.TryGetValue(transferId, out var r)
                    ? r
                    : _currentRoomId;
                if (string.IsNullOrEmpty(punchRoom))
                    return;
                await _ws.RequestFilePunchAsync(punchRoom, transferId);
            });
        }
    }

    private bool ShouldContinuePunchWatch(string transferId)
    {
        if (_transferLocalPaths.TryGetValue(transferId, out var known) && File.Exists(known))
            return false;
        if (_recipientSavePaths.TryGetValue(transferId, out var savePath)
            && File.Exists(savePath)
            && new FileInfo(savePath).Length == _transferSizes.GetValueOrDefault(transferId))
            return false;
        var m = _messages.FirstOrDefault(x => x.FileTransferId == transferId);
        if (_recipientReceiveCts.ContainsKey(transferId))
            return false;
        if (m != null)
        {
            if (m.IsMine)
                return false;
            if (m.FileStatus is "completed" or "failed" or "rejected" or "cancelled" or "paused" or "pending")
                return false;
            if (m.IsSnapshot && TryGetImagePath(m, out _))
                return false;
            return true;
        }

        return _recipientSavePaths.ContainsKey(transferId) || _transferStore.GetReceiver(transferId) != null;
    }

    private string? TransferRoomId(string transferId)
    {
        if (_transferRoomIds.TryGetValue(transferId, out var room))
            return room;
        var m = _messages.FirstOrDefault(x => x.FileTransferId == transferId);
        if (m != null && !string.IsNullOrEmpty(m.RoomId))
            return ChatIds.NormalizeRoomId(m.RoomId);
        return _currentRoomId;
    }

    private async Task AcceptIncomingFileTransferCoreAsync(string roomId, string transferId, string fileName, bool isSnapshot)
    {
        if (!isSnapshot)
            return;
        var normRoom = ChatIds.NormalizeRoomId(roomId);
        var savePath = SnapshotPaths.ReceivedFilePath(transferId, fileName);
        _recipientSavePaths[transferId] = savePath;
        _punchWatchRooms[transferId] = normRoom;
        if (!await _api.TryRespondFileTransferAsync(transferId, true))
            return;
        if (_pendingFilePunches.TryGetValue(transferId, out var pending))
        {
            _pendingFilePunches.Remove(transferId);
            await OnFilePunch(pending);
            return;
        }

        await _ws.RequestFilePunchAsync(normRoom, transferId);
        SchedulePunchWatch(transferId, normRoom);
    }

    private void NotifyIncomingFileTransfer(string senderUserId, string fileName, bool isSnapshot)
    {
        var preview = isSnapshot ? "📷 Snapshot" : $"📎 {fileName}";
        ShowIncomingMessageNotification(senderUserId, preview);
    }

    private string LookupContactLoginId(string userId)
    {
        var norm = ChatIds.NormalizeUserId(userId);
        return _contacts.FirstOrDefault(c => ChatIds.NormalizeUserId(c.Id) == norm)?.LoginId ?? norm;
    }

    private async Task AutoAcceptPendingSnapshotsInRoomAsync()
    {
        var pending = _messages
            .Where(x => x.IsSnapshot && !x.IsMine && x.FileStatus == "pending" && x.FileTransferId != null)
            .ToList();
        foreach (var m in pending)
        {
            if (_recipientSavePaths.ContainsKey(m.FileTransferId!))
                continue;
            var savePath = SnapshotPaths.ReceivedFilePath(m.FileTransferId!, m.FileName);
            await AcceptIncomingFileAsync(m, savePath);
        }
    }

    private async Task MergeRoomFileTransfersAsync(string roomId)
    {
        try
        {
            var transfers = await _api.LoadRoomFileTransfersAsync(roomId);
            var normMe = ChatIds.NormalizeUserId(_me.id);
            foreach (var raw in transfers)
            {
                var t = TransferJson.Parse(raw, roomId);
                var isMine = t.SenderUserId == normMe;
                if (!isMine && IsChatRoomInView(roomId))
                    await MarkTransferDeliveredIfNeededAsync(t.Id, TransferJson.RowForViewer(t, _me.id, false));
                UpsertFileMessage(raw, fallbackRoomId: roomId, render: false);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"파일 전송 목록 불러오기 실패: {ex.Message}");
        }
    }

    private string? ResolveStoredImagePath(string transferId, string fileName, string status)
    {
        if (_transferLocalPaths.TryGetValue(transferId, out var known) && File.Exists(known))
            return known;
        if (status == "completed")
        {
            var single = _transferStore.GetReceiver(transferId)?.TargetPaths
                         ?? _transferStore.GetSender(transferId)?.LocalPaths;
            if (single is { Count: 1 } && File.Exists(single[0]))
                return single[0];
        }
        var received = SnapshotPaths.ReceivedFilePath(transferId, fileName);
        if (File.Exists(received))
            return received;
        var discovered = SnapshotPaths.TryFindPersistedPath(transferId);
        if (discovered != null)
            return discovered;
        if (status != "completed")
            return null;
        if (_senderFilePaths.TryGetValue(transferId, out var sent) && File.Exists(sent))
            return sent;
        return null;
    }

    private bool IsChatRoomInView(string roomId)
    {
        // Only an active (foreground) window counts as "seen"; a window left behind other
        // apps or sitting in the taskbar must not send read receipts.
        if (string.IsNullOrEmpty(roomId) || !IsVisible || !IsActive)
            return false;
        if (WindowState == WindowState.Minimized)
            return false;
        return IsUiRoomActive(roomId);
    }

    private static bool IsUiRoomActive(string? roomId, string? currentRoomId) =>
        ChatIds.NormalizeRoomId(roomId ?? "") == ChatIds.NormalizeRoomId(currentRoomId ?? "");

    private bool IsUiRoomActive(string? roomId) => IsUiRoomActive(roomId, _currentRoomId);

    private async Task MarkCurrentRoomReadIfViewingAsync()
    {
        if (_currentRoomId == null || !IsChatRoomInView(_currentRoomId))
            return;
        var roomId = _currentRoomId;
        var lastOther = _messages.LastOrDefault(m => !m.IsMine && !m.IsFileTransfer);
        if (lastOther != null)
        {
            try
            {
                await _ws.MarkReadAsync(roomId, lastOther.Id);
                await _api.MarkReadAsync(roomId, lastOther.Id);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"읽음 표시 실패: {ex.Message}");
            }
        }

        await MarkVisibleTransfersSeenAsync(roomId);
        if (_currentPeerId != null)
            ZeroUnreadBadgeForPeer(_currentPeerId);
    }

    /// <summary>Incoming files/snapshots count as seen (sender gets ✓✓) only once the window is in front.</summary>
    private async Task MarkVisibleTransfersSeenAsync(string roomId)
    {
        var unseen = _messages
            .Where(m => m.IsFileTransfer && !m.IsMine && !m.PeerDelivered && !string.IsNullOrEmpty(m.FileTransferId)
                        && IsUiRoomActive(m.RoomId, roomId))
            .ToList();
        foreach (var m in unseen)
        {
            if (!_deliveredMarked.Add(m.FileTransferId!))
                continue;
            try
            {
                await _api.MarkFileTransferDeliveredAsync(m.FileTransferId!);
                m.PeerDelivered = true;
            }
            catch (Exception ex)
            {
                _deliveredMarked.Remove(m.FileTransferId!);
                AppLog.Warn($"파일 확인 표시 실패 ({m.FileTransferId}): {ex.Message}");
            }
        }
    }

    private const double BubbleMaxWidth = 480;
    private static readonly TimeSpan MessageGroupWindow = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, TransferView> _transferViews = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTime Stamp, System.Windows.Media.Imaging.BitmapSource Image)> _imageCache =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _forceScrollToEnd = true;
    private bool _stickToBottom = true;

    /// <summary>Live parts of a rendered file card, so progress ticks update it without a full re-render.</summary>
    private sealed class TransferView
    {
        public required ChatMessage Message { get; init; }
        public required TextBlock Glyph { get; init; }
        public required TextBlock Status { get; init; }
        public required ProgressBar Progress { get; init; }
    }

    private void OnMessageScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            if (_stickToBottom)
                MessageScroll.ScrollToEnd();
            return;
        }

        _stickToBottom = MessageScroll.ScrollableHeight - MessageScroll.VerticalOffset <= 40;
    }

    private void RenderMessages()
    {
        MessageList.Items.Clear();
        _transferViews.Clear();
        DateTime? lastDay = null;
        for (var i = 0; i < _messages.Count; i++)
        {
            var m = _messages[i];
            var day = ChatTimestamps.ToCompanyLocal(m.SentAt).Date;
            if (lastDay != day)
            {
                MessageList.Items.Add(BuildDateChip(m.SentAt));
                lastDay = day;
            }

            var joinsPrevious = i > 0 && IsSameMessageGroup(_messages[i - 1], m);
            var joinsNext = i + 1 < _messages.Count && IsSameMessageGroup(m, _messages[i + 1]);
            MessageList.Items.Add(BuildMessageRow(m, joinsPrevious, joinsNext));
        }

        UpdateMessageSelectionChrome();
        if (_forceScrollToEnd)
        {
            _forceScrollToEnd = false;
            _stickToBottom = true;
            MessageScroll.ScrollToEnd();
        }
    }

    private static bool IsSameMessageGroup(ChatMessage a, ChatMessage b) =>
        a.IsMine == b.IsMine
        && (b.SentAt - a.SentAt).Duration() <= MessageGroupWindow
        && ChatTimestamps.ToCompanyLocal(a.SentAt).Date == ChatTimestamps.ToCompanyLocal(b.SentAt).Date;

    private static UIElement BuildDateChip(DateTime sentAt)
    {
        var text = new TextBlock
        {
            Text = ChatTimestamps.FormatDayHeader(sentAt),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "DateChipTextBrush");
        var chip = new Border
        {
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(11, 3, 11, 4),
            Margin = new Thickness(0, 12, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = text,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "DateChipBrush");
        return chip;
    }

    private UIElement BuildMessageRow(ChatMessage m, bool joinsPrevious, bool joinsNext)
    {
        var isSelected = _selectedMessageIds.Contains(m.Id);
        var align = m.IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var isImage = m.IsFileTransfer && m.IsImageTransfer;
        var emojiOnly = !m.IsFileTransfer
                        && ChatEmojiHelper.IsEmojiOnlyMessage(ComposeInputHelper.NormalizeNewlines(m.PlainText ?? ""));

        // Grouped bubbles share a flat inner corner; the last one in a group gets the "tail" corner.
        const double round = 16, inner = 6, tail = 3;
        var top = joinsPrevious ? inner : round;
        var bottom = joinsNext ? inner : tail;
        var corners = m.IsMine
            ? new CornerRadius(round, top, bottom, round)
            : new CornerRadius(top, round, round, bottom);

        var bubble = new Border
        {
            CornerRadius = corners,
            Padding = emojiOnly ? new Thickness(2)
                : isImage ? new Thickness(4)
                : m.IsFileTransfer ? new Thickness(10, 10, 12, 7)
                : new Thickness(12, 7, 10, 7),
            MaxWidth = BubbleMaxWidth,
            HorizontalAlignment = align,
            Tag = m,
            Cursor = m.IsFileTransfer ? Cursors.Hand : Cursors.Arrow,
            BorderThickness = new Thickness(m.IsMine || emojiOnly ? 0 : 1),
        };
        if (emojiOnly)
            bubble.Background = Brushes.Transparent;
        else
            bubble.SetResourceReference(Border.BackgroundProperty, m.IsMine ? "BubbleMineBrush" : "BubbleTheirsBrush");
        bubble.SetResourceReference(Border.BorderBrushProperty, "BubbleTheirsBorderBrush");
        bubble.SetResourceReference(TextElement.ForegroundProperty,
            emojiOnly ? "TextPrimaryBrush" : m.IsMine ? "BubbleMineTextBrush" : "BubbleTheirsTextBrush");
        bubble.Child = m.IsFileTransfer
            ? isImage ? BuildImageBubbleContent(m) : BuildFileBubbleContent(m)
            : BuildTextBubbleContent(m, emojiOnly);

        bubble.PreviewMouseLeftButtonDown += OnBubblePreviewMouseDown;
        bubble.PreviewMouseMove += OnBubblePreviewMouseMove;
        bubble.PreviewMouseLeftButtonUp += OnBubblePreviewMouseUp;
        if (m.IsFileTransfer)
            bubble.MouseRightButtonUp += OnFileBubbleRightClick;
        else
            bubble.MouseRightButtonUp += OnMessageBubbleRightClick;

        var row = new Grid
        {
            Margin = new Thickness(0, joinsPrevious ? 1 : 5, 0, joinsNext ? 1 : 3),
        };
        if (isSelected)
            row.SetResourceReference(Panel.BackgroundProperty, "AccentSoftBrush");
        var bubbleHost = WrapBubbleWithSelectionChrome(bubble, isSelected, align);
        if (_selected?.IsGroup == true && !m.IsMine)
        {
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Left };
            stack.Children.Add(bubbleHost);
            var senderId = LookupContactLoginId(m.SenderUserId);
            if (!string.IsNullOrWhiteSpace(senderId))
            {
                var name = new TextBlock
                {
                    Text = senderId,
                    FontSize = 11.5,
                    Margin = new Thickness(4, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                };
                name.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                stack.Children.Add(name);
            }
            row.Children.Add(stack);
        }
        else
        {
            row.Children.Add(bubbleHost);
        }
        return row;
    }

    private static UIElement WrapBubbleWithSelectionChrome(Border bubble, bool isSelected, HorizontalAlignment align)
    {
        if (!isSelected)
            return bubble;
        var host = new Grid
        {
            HorizontalAlignment = align,
            MaxWidth = bubble.MaxWidth,
            Margin = new Thickness(0, 3, 0, 3),
        };
        host.Children.Add(bubble);
        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -6, 0),
            Child = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = (FontFamily)Application.Current.FindResource("IconFont"),
                Foreground = Brushes.White,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        badge.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        badge.SetResourceReference(Border.BorderBrushProperty, "SurfaceBrush");
        Panel.SetZIndex(badge, 2);
        host.Children.Add(badge);
        return host;
    }

    private static UIElement BuildTextBubbleContent(ChatMessage m, bool emojiOnly)
    {
        if (ChatHistoryClipboard.TrySplitReply(m.PlainText, out var quote, out var replyText))
            return BuildReplyBubbleContent(m, quote, replyText);

        if (emojiOnly)
        {
            var stack = new StackPanel
            {
                HorizontalAlignment = m.IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            };
            var emojiText = ChatMessageTextHelper.CreateMessageContent(m.PlainText, ownMessage: m.IsMine);
            emojiText.HorizontalAlignment = m.IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            stack.Children.Add(emojiText);
            var chip = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 1, 7, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                Child = BuildMeta(m, "DateChipTextBrush"),
            };
            chip.SetResourceReference(Border.BackgroundProperty, "DateChipBrush");
            stack.Children.Add(chip);
            return stack;
        }

        // Short one-line messages keep the time on the same row. Anything wider gets the time
        // on its own line, so it never paints over the text.
        var text = ChatMessageTextHelper.CreateMessageContent(m.PlainText, ownMessage: m.IsMine);
        var meta = BuildMeta(m);
        const double innerMax = BubbleMaxWidth - 22;
        const double gap = 8;
        var singleLine = !ComposeInputHelper.NormalizeNewlines(m.PlainText ?? "").Contains('\n');
        meta.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var fitsInline = singleLine
                         && text.DesiredSize.Width + gap + meta.DesiredSize.Width <= innerMax;

        if (fitsInline)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(text);
            Grid.SetColumn(meta, 1);
            meta.Margin = new Thickness(gap, 6, 0, -1);
            meta.VerticalAlignment = VerticalAlignment.Bottom;
            row.Children.Add(meta);
            return row;
        }

        var body = new StackPanel();
        body.Children.Add(text);
        meta.HorizontalAlignment = HorizontalAlignment.Right;
        meta.Margin = new Thickness(16, 1, 0, -1);
        body.Children.Add(meta);
        return body;
    }

    /// <summary>Quoted message, a light rule, then the reply. The same body is what the other side receives.</summary>
    private static UIElement BuildReplyBubbleContent(ChatMessage m, string quote, string replyText)
    {
        var quoteBreak = quote.IndexOf('\n');
        var header = quoteBreak < 0 ? quote : quote[..quoteBreak];
        var quoted = quoteBreak < 0 ? "" : quote[(quoteBreak + 1)..];
        var metaBrush = m.IsMine ? "BubbleMineMetaBrush" : "BubbleTheirsMetaBrush";

        var stack = new StackPanel { MinWidth = 140 };
        var quoteHeader = new TextBlock
        {
            Text = header,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        quoteHeader.SetResourceReference(TextBlock.ForegroundProperty, metaBrush);
        stack.Children.Add(quoteHeader);

        if (quoted.Length > 0)
        {
            var quotedText = ChatMessageTextHelper.CreateMessageContent(quoted, ownMessage: m.IsMine);
            quotedText.Margin = new Thickness(0, 2, 0, 0);
            quotedText.SetResourceReference(TextElement.ForegroundProperty, metaBrush);
            stack.Children.Add(quotedText);
        }

        var rule = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 8, 0, 8),
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        rule.SetResourceReference(Border.BackgroundProperty, metaBrush);
        stack.Children.Add(rule);

        if (replyText.Length > 0)
            stack.Children.Add(ChatMessageTextHelper.CreateMessageContent(replyText, ownMessage: m.IsMine));

        var meta = BuildMeta(m);
        meta.HorizontalAlignment = HorizontalAlignment.Right;
        meta.Margin = new Thickness(16, 2, 0, -1);
        stack.Children.Add(meta);
        return stack;
    }

    private static TextBlock BuildMeta(ChatMessage m, string? brushKey = null)
    {
        var meta = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.NoWrap,
            ToolTip = ChatTimestamps.Format(m.SentAt),
        };
        meta.SetResourceReference(TextBlock.ForegroundProperty,
            brushKey ?? (m.IsMine ? "BubbleMineMetaBrush" : "BubbleTheirsMetaBrush"));
        meta.Inlines.Add(new Run(ChatTimestamps.FormatShortTime(m.SentAt)));
        if (m.EditedAt != null)
            meta.Inlines.Add(new Run(" · edited") { FontSize = 10 });
        var ticks = MetaLabel(m);
        if (ticks.Length > 0)
        {
            var tick = new Run(" " + ticks);
            if (brushKey == null && ticks == "✓✓")
                tick.SetResourceReference(TextElement.ForegroundProperty, "ReadReceiptMineBrush");
            meta.Inlines.Add(tick);
        }

        return meta;
    }

    private UIElement BuildImageBubbleContent(ChatMessage m)
    {
        var stack = new StackPanel();
        TryGetImagePath(m, out var path);
        var bitmap = path != null ? LoadCachedImage(path) : null;
        var hasCaption = !string.IsNullOrWhiteSpace(m.Caption);
        var completed = m.FileStatus == "completed";
        var metaOnImage = bitmap != null && completed && !hasCaption;

        if (bitmap != null)
        {
            var (w, h) = FitImage(bitmap.Width, bitmap.Height, 320, 280);
            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            host.Children.Add(new Border
            {
                Width = w,
                Height = h,
                CornerRadius = new CornerRadius(12),
                Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill },
            });
            if (metaOnImage)
            {
                var meta = BuildMeta(m);
                meta.Foreground = Brushes.White;
                foreach (var inline in meta.Inlines)
                    inline.ClearValue(TextElement.ForegroundProperty);
                host.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(7, 1, 7, 2),
                    Margin = new Thickness(0, 0, 6, 6),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Child = meta,
                });
            }

            stack.Children.Add(host);
        }

        if (bitmap == null || !completed)
        {
            var card = BuildFileCard(m, m.IsSnapshot ? "Snapshot" : "Image", null);
            card.Margin = new Thickness(6, bitmap == null ? 6 : 8, 8, 0);
            stack.Children.Add(card);
        }

        if (bitmap == null && completed)
        {
            var note = new TextBlock
            {
                Text = m.IsMine ? "Cannot show preview." : "File not found. Save again.",
                FontSize = 12,
                Margin = new Thickness(62, 2, 8, 0),
                TextWrapping = TextWrapping.Wrap,
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, m.IsMine ? "BubbleMineMetaBrush" : "BubbleTheirsMetaBrush");
            stack.Children.Add(note);
        }

        AddCaption(stack, m, new Thickness(8, 6, 8, 0));
        if (!metaOnImage)
        {
            var meta = BuildMeta(m);
            meta.HorizontalAlignment = HorizontalAlignment.Right;
            meta.Margin = new Thickness(8, 2, 8, 3);
            stack.Children.Add(meta);
        }

        return stack;
    }

    private UIElement BuildFileBubbleContent(ChatMessage m)
    {
        var stack = new StackPanel { MinWidth = 220 };
        stack.Children.Add(BuildFileCard(m, FileTransferLabels.TitleText(m), FileTransferLabels.ItemsTooltip(m)));
        AddCaption(stack, m, new Thickness(0, 8, 0, 0));
        var meta = BuildMeta(m);
        meta.HorizontalAlignment = HorizontalAlignment.Right;
        meta.Margin = new Thickness(0, 3, -2, 0);
        stack.Children.Add(meta);
        return stack;
    }

    /// <summary>Icon circle (doubles as the click action hint), title, "size · status" and a thin progress bar.</summary>
    private FrameworkElement BuildFileCard(ChatMessage m, string title, string? tooltip)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = new TextBlock
        {
            Text = FileCardGlyph(m),
            FontFamily = (FontFamily)FindResource("IconFont"),
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "OnAccentBrush");
        var icon = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(22),
            VerticalAlignment = VerticalAlignment.Center,
            Child = glyph,
        };
        icon.SetResourceReference(Border.BackgroundProperty, m.IsMine ? "BubbleMineSoftBrush" : "AccentBrush");
        grid.Children.Add(icon);

        var info = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 320,
            ToolTip = tooltip,
        });
        var status = new TextBlock
        {
            Text = FileCardStatusLine(m),
            FontSize = 12.5,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        status.SetResourceReference(TextBlock.ForegroundProperty, m.IsMine ? "BubbleMineMetaBrush" : "BubbleTheirsMetaBrush");
        info.Children.Add(status);
        var progress = new ProgressBar
        {
            Value = FileTransferLabels.Percent(m),
            Margin = new Thickness(0, 6, 0, 2),
            Visibility = IsTransferActive(m) ? Visibility.Visible : Visibility.Collapsed,
        };
        progress.SetResourceReference(StyleProperty, "ThinProgressBarStyle");
        progress.SetResourceReference(BackgroundProperty, m.IsMine ? "BubbleMineSoftBrush" : "ProgressTrackBrush");
        progress.SetResourceReference(ForegroundProperty, m.IsMine ? "BubbleMineTextBrush" : "AccentBrush");
        info.Children.Add(progress);
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        if (!string.IsNullOrEmpty(m.FileTransferId))
        {
            _transferViews[m.FileTransferId] = new TransferView
            {
                Message = m,
                Glyph = glyph,
                Status = status,
                Progress = progress,
            };
        }

        return grid;
    }

    private void UpdateTransferView(TransferView view)
    {
        var m = view.Message;
        view.Glyph.Text = FileCardGlyph(m);
        view.Status.Text = FileCardStatusLine(m);
        view.Progress.Value = FileTransferLabels.Percent(m);
        view.Progress.Visibility = IsTransferActive(m) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsTransferActive(ChatMessage m) =>
        m.FileStatus is "accepted" or "in_progress" or "paused";

    private string FileCardStatusLine(ChatMessage m)
    {
        var size = FileSizeFormatter.Format(m.FileSizeBytes);
        var status = FileTransferLabels.Status(m, IsSenderOnline(m));
        return string.IsNullOrEmpty(status) ? size : $"{size} · {status}";
    }

    private static string FileCardGlyph(ChatMessage m) =>
        m.FileStatus switch
        {
            "pending" when !m.IsMine => "\uE896",
            "accepted" or "in_progress" => "\uE769",
            "paused" => "\uE768",
            "cancelled" or "rejected" or "expired" or "failed" => "\uE711",
            _ when m.IsImageTransfer => "\uEB9F",
            _ when m.IsFolder => "\uE8B7",
            _ => "\uE8A5",
        };

    private static void AddCaption(StackPanel stack, ChatMessage m, Thickness margin)
    {
        if (string.IsNullOrWhiteSpace(m.Caption))
            return;
        var caption = ChatMessageTextHelper.CreateMessageContent(m.Caption, ownMessage: m.IsMine);
        caption.Margin = margin;
        stack.Children.Add(caption);
    }

    private static (double W, double H) FitImage(double w, double h, double maxW, double maxH)
    {
        if (w <= 0 || h <= 0)
            return (maxW, maxH);
        var scale = Math.Min(1, Math.Min(maxW / w, maxH / h));
        return (Math.Max(80, w * scale), Math.Max(60, h * scale));
    }

    private System.Windows.Media.Imaging.BitmapSource? LoadCachedImage(string path)
    {
        DateTime stamp;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return null;
        }

        if (_imageCache.TryGetValue(path, out var hit) && hit.Stamp == stamp)
            return hit.Image;
        var image = ScreenCaptureService.TryLoadImage(path);
        if (image != null)
            _imageCache[path] = (stamp, image);
        return image;
    }

    private static string MetaLabel(ChatMessage m)
    {
        if (!m.IsMine)
            return "";
        return m.IsFileTransfer ? FileDeliveryLabel(m) : DeliveryLabel(m);
    }

    // Snapshots are received automatically, so only an explicit "seen" counts for them;
    // a file the peer accepted was necessarily looked at.
    private static string FileDeliveryLabel(ChatMessage m) =>
        m.PeerDelivered
        || (!m.IsSnapshot && m.FileStatus is "completed" or "accepted" or "in_progress" or "paused")
            ? "✓✓"
            : "✓";

    private void OnBubblePreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ChatMessage m })
            return;
        if (Keyboard.Modifiers is ModifierKeys.Control or ModifierKeys.Shift)
        {
            if (m.IsFileTransfer)
                return;
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _selectionAnchorMessageId != null)
                SelectMessageRange(_selectionAnchorMessageId, m.Id);
            else
                ToggleMessageSelection(m.Id);
            _messageSelectionMode = _selectedMessageIds.Count > 0;
            RenderMessages();
            return;
        }

        _longPressMessage = m;
        _longPressActivated = false;
        _longPressStart = e.GetPosition(this);
        _longPressTimer?.Stop();
        _longPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(520) };
        _longPressTimer.Tick += OnLongPressTimerTick;
        _longPressTimer.Start();
    }

    private void OnBubblePreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_longPressTimer is not { IsEnabled: true } || _longPressMessage == null)
            return;
        var pos = e.GetPosition(this);
        var dx = pos.X - _longPressStart.X;
        var dy = pos.Y - _longPressStart.Y;
        if (dx * dx + dy * dy <= 36)
            return;
        _longPressTimer.Stop();
        _longPressMessage = null;
    }

    private void OnLongPressTimerTick(object? sender, EventArgs e)
    {
        _longPressTimer?.Stop();
        if (_longPressMessage == null)
            return;
        _longPressActivated = true;
        _messageSelectionMode = true;
        if (!_selectedMessageIds.Contains(_longPressMessage.Id))
            ToggleMessageSelection(_longPressMessage.Id);
        UpdateMessageSelectionChrome();
        RenderMessages();
    }

    private async void OnBubblePreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _longPressTimer?.Stop();
        if (sender is not Border { Tag: ChatMessage m })
            return;

        if (_longPressActivated)
        {
            _longPressActivated = false;
            _longPressMessage = null;
            e.Handled = true;
            return;
        }

        _longPressMessage = null;

        if (_messageSelectionMode)
        {
            e.Handled = true;
            ToggleMessageSelection(m.Id);
            if (_selectedMessageIds.Count == 0)
                _messageSelectionMode = false;
            RenderMessages();
            return;
        }

        if (m.IsFileTransfer)
        {
            e.Handled = true;
            await HandleFileBubbleClickAsync(m);
        }
    }

    private async Task HandleFileBubbleClickAsync(ChatMessage m)
    {
        if (m.IsImageTransfer && m.FileStatus == "completed" && TryGetImagePath(m, out var previewPath))
        {
            new ImagePreviewWindow(previewPath, this).ShowDialog();
            return;
        }

        if (string.IsNullOrEmpty(m.FileTransferId))
            return;
        switch (m.FileStatus)
        {
            case "completed":
                ShowTransferInExplorer(m);
                break;
            case "pending" when !m.IsMine:
                await SaveIncomingTransferAsync(m);
                break;
            case "accepted" or "in_progress":
                await PauseFileTransferAsync(m);
                break;
            case "paused":
                await ResumeFileTransferAsync(m);
                break;
        }
    }

    /// <summary>
    /// What to highlight in Explorer: the saved folder for a folder bundle (it may have been
    /// renamed "name (1)"), otherwise the first file that still exists.
    /// </summary>
    private string? ResolveTransferLocation(ChatMessage m)
    {
        var id = m.FileTransferId!;
        var paths = m.IsMine
            ? _transferStore.GetSender(id)?.LocalPaths
            : _transferStore.GetReceiver(id)?.TargetPaths;
        if ((paths == null || paths.Count == 0) && _transferLocalPaths.TryGetValue(id, out var single))
            paths = [single];
        if (paths == null || paths.Count == 0)
            return null;

        if (m.IsFolder && m.Items.Count > 0)
        {
            var depth = m.Items[0].Path.Replace('\\', '/').Split('/').Length - 1;
            var top = paths[0];
            for (var i = 0; i < depth; i++)
                top = Path.GetDirectoryName(top) ?? top;
            if (Directory.Exists(top))
                return top;
        }

        return paths.FirstOrDefault(File.Exists);
    }

    private void ShowTransferInExplorer(ChatMessage m)
    {
        var target = ResolveTransferLocation(m);
        if (target == null)
        {
            AppDialog.Show("File not found. It may have been moved or deleted.", "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{target}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"탐색기 열기 실패: {ex.Message}");
        }
    }

    private async Task SaveIncomingTransferAsync(ChatMessage m)
    {
        if (m.IsMine || m.FileStatus != "pending" || string.IsNullOrEmpty(m.FileTransferId))
            return;
        if (m.IsSnapshot)
        {
            await AcceptIncomingFileAsync(m, SnapshotPaths.ReceivedFilePath(m.FileTransferId, m.FileName));
            return;
        }

        var dlg = new OpenFolderDialog { Title = "Choose a folder to save to" };
        if (dlg.ShowDialog(this) != true || string.IsNullOrEmpty(dlg.FolderName))
            return;
        List<string> targets;
        try
        {
            targets = TransferTargetPlanner.Plan(dlg.FolderName, m.Items);
        }
        catch (Exception ex)
        {
            AppDialog.Show(UserErrorMessages.ForUi(ex, "save"), "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _transferStore.SetReceiver(new ReceiverTransferState
        {
            TransferId = m.FileTransferId,
            TargetPaths = targets,
            Sizes = m.Items.Select(i => i.Size).ToList(),
        });
        await AcceptIncomingFileAsync(m, dlg.FolderName);
    }

    private async Task PauseFileTransferAsync(ChatMessage m)
    {
        var transferId = m.FileTransferId!;
        var bytesDone = m.BytesDone;
        var receiver = _transferStore.GetReceiver(transferId);
        if (!m.IsMine && receiver != null)
            bytesDone = FileTransferTcpService.BytesOnDisk(receiver.TargetPaths, receiver.Sizes);
        m.FileStatus = "paused";
        AbortLocalFileTransfer(transferId);
        RenderMessages();
        try
        {
            UpsertFileMessage(await _api.PauseFileTransferAsync(transferId, bytesDone));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"파일 일시정지 실패 ({transferId}): {ex.Message}");
        }
    }

    private async Task ResumeFileTransferAsync(ChatMessage m)
    {
        var transferId = m.FileTransferId!;
        try
        {
            m.FileStatus = "accepted";
            RenderMessages();
            var updated = await _api.ResumeFileTransferAsync(transferId);
            UpsertFileMessage(updated);
            if (m.IsMine)
                await StartSenderTransferAsync(transferId, m.FileName);
            else if (m.IsSnapshot)
                await EnsureIncomingSnapshotReceivingAsync(m);
            else
                await EnsureIncomingBundleReceivingAsync(transferId, m.RoomId);
        }
        catch (Exception ex)
        {
            m.FileStatus = "paused";
            RenderMessages();
            AppLog.Warn($"파일 재개 실패 ({transferId}): {ex.Message}");
        }
    }

    private void OnMessageBubbleRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ChatMessage m } || m.IsFileTransfer) return;
        if (_currentRoomId == null) return;
        e.Handled = true;
        if (sender is FrameworkElement fe)
            ShowMessageContextMenu(m, fe);
    }

    private void OnMessageScrollContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_currentRoomId == null || _messages.Count == 0)
        {
            e.Handled = true;
            return;
        }

        var menu = new ContextMenu();
        var copyAll = new MenuItem { Header = "Copy entire chat" };
        copyAll.Click += (_, _) => CopyMessagesToClipboard(_messages);
        menu.Items.Add(copyAll);
        if (_selectedMessageIds.Count > 0)
        {
            menu.Items.Add(new Separator());
            var copySel = new MenuItem { Header = $"Copy {_selectedMessageIds.Count} selected" };
            copySel.Click += (_, _) => CopyMessagesToClipboard(GetSelectedMessages(), bodyOnly: true);
            menu.Items.Add(copySel);
        }

        MessageScroll.ContextMenu = menu;
    }

    private void ShowMessageContextMenu(ChatMessage anchor, FrameworkElement placementTarget)
    {
        var targets = GetContextTargetMessages(anchor);
        var textTargets = targets.Where(m => !m.IsFileTransfer).ToList();
        var menu = new ContextMenu { PlacementTarget = placementTarget };
        var selectedText = ChatMessageTextHelper.FindSelectedText(placementTarget);
        if (!string.IsNullOrEmpty(selectedText))
        {
            var copySelection = new MenuItem { Header = "Copy selection" };
            var snippet = selectedText;
            copySelection.Click += (_, _) =>
            {
                try { Clipboard.SetText(snippet); }
                catch (Exception ex) { AppLog.Warn($"선택 복사 실패: {ex.Message}"); }
            };
            menu.Items.Add(copySelection);
        }

        if (textTargets.Count > 0)
        {
            var copy = new MenuItem
            {
                Header = textTargets.Count > 1 ? $"Copy {textTargets.Count} messages" : "Copy",
            };
            copy.Click += (_, _) => CopyMessagesToClipboard(textTargets, bodyOnly: true);
            menu.Items.Add(copy);

            var reply = new MenuItem { Header = "Reply" };
            reply.Click += (_, _) => BeginReply(anchor);
            menu.Items.Add(reply);

            if (textTargets.Count == 1 && CanEditMessage(textTargets[0]))
            {
                var edit = new MenuItem { Header = "Edit" };
                edit.Click += (_, _) => BeginEdit(textTargets[0]);
                menu.Items.Add(edit);
            }

            var delete = new MenuItem
            {
                Header = textTargets.Count > 1 ? $"Delete {textTargets.Count} messages" : "Delete",
            };
            delete.SetResourceReference(StyleProperty, "DangerMenuItemStyle");
            delete.Click += async (_, _) => await DeleteMessagesAsync(textTargets);
            menu.Items.Add(delete);
        }

        menu.Items.Add(new Separator());
        var select = new MenuItem { Header = "Select" };
        select.Click += (_, _) => EnterSelectionMode(anchor);
        menu.Items.Add(select);

        if (menu.Items.Count == 0)
            return;

        placementTarget.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private List<ChatMessage> GetContextTargetMessages(ChatMessage anchor)
    {
        if (_selectedMessageIds.Contains(anchor.Id) && _selectedMessageIds.Count > 0)
            return GetSelectedMessages();
        return new List<ChatMessage> { anchor };
    }

    private List<ChatMessage> GetSelectedMessages() =>
        _messages.Where(m => _selectedMessageIds.Contains(m.Id)).OrderBy(m => m.SentAt).ToList();

    private void ToggleMessageSelection(string messageId)
    {
        if (!_selectedMessageIds.Add(messageId))
            _selectedMessageIds.Remove(messageId);
        _selectionAnchorMessageId = messageId;
    }

    private void EnterSelectionMode(ChatMessage anchor)
    {
        _messageSelectionMode = true;
        if (!_selectedMessageIds.Contains(anchor.Id))
            ToggleMessageSelection(anchor.Id);
        UpdateMessageSelectionChrome();
        RenderMessages();
    }

    private void SelectMessageRange(string fromId, string toId)
    {
        var ordered = _messages.OrderBy(m => m.SentAt).ToList();
        var fromIdx = ordered.FindIndex(m => m.Id == fromId);
        var toIdx = ordered.FindIndex(m => m.Id == toId);
        if (fromIdx < 0 || toIdx < 0) return;
        var start = Math.Min(fromIdx, toIdx);
        var end = Math.Max(fromIdx, toIdx);
        for (var i = start; i <= end; i++)
            _selectedMessageIds.Add(ordered[i].Id);
        _selectionAnchorMessageId = toId;
    }

    private void UpdateMessageSelectionChrome()
    {
        var count = _selectedMessageIds.Count;
        if (count == 0 && !_messageSelectionMode)
        {
            MessageSelectionPanel.Visibility = Visibility.Collapsed;
            ChatMenuButton.Visibility = Visibility.Visible;
            return;
        }

        MessageSelectionPanel.Visibility = Visibility.Visible;
        ChatMenuButton.Visibility = Visibility.Collapsed;
        MessageSelectionLabel.Text = count == 0 ? "Select messages" : $"{count} selected";
    }

    private void OnClearMessageSelection(object sender, RoutedEventArgs e) => ClearMessageSelection();

    private void ClearMessageSelection()
    {
        _selectedMessageIds.Clear();
        _selectionAnchorMessageId = null;
        _messageSelectionMode = false;
        UpdateMessageSelectionChrome();
        RenderMessages();
    }

    private void OnSelectAllMessages(object sender, RoutedEventArgs e)
    {
        _messageSelectionMode = true;
        foreach (var m in _messages)
            _selectedMessageIds.Add(m.Id);
        if (_messages.Count > 0)
            _selectionAnchorMessageId = _messages.OrderBy(m => m.SentAt).First().Id;
        UpdateMessageSelectionChrome();
        RenderMessages();
    }

    private void OnCopySelectedMessages(object sender, RoutedEventArgs e) =>
        CopyMessagesToClipboard(GetSelectedMessages(), bodyOnly: true);

    private async void OnDeleteSelectedMessages(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedMessages();
        if (targets.Count == 0) return;
        await DeleteMessagesAsync(targets);
        if (_selectedMessageIds.Count == 0)
            _messageSelectionMode = false;
        UpdateMessageSelectionChrome();
    }

    private void CopyMessagesToClipboard(IEnumerable<ChatMessage> messages, bool bodyOnly = false)
    {
        var list = messages.OrderBy(m => m.SentAt).ToList();
        if (list.Count == 0) return;
        var text = bodyOnly
            ? ChatHistoryClipboard.FormatBodies(list)
            : ChatHistoryClipboard.FormatMany(list, _me.login_id, PeerDisplayName());
        if (text.Length == 0)
            return;
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"클립보드 Copy failed: {ex.Message}");
            AppDialog.Show("Could not copy to clipboard.", "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string PeerLoginId() => _selected?.LoginId ?? "peer";

    private string PeerDisplayName() => PeerLoginId();

    private async Task DeleteMessagesAsync(List<ChatMessage> targets)
    {
        if (_currentRoomId == null || targets.Count == 0) return;
        var mineCount = targets.Count(m => m.IsMine);
        var otherCount = targets.Count - mineCount;
        string prompt;
        if (mineCount > 0 && otherCount == 0)
        {
            prompt = mineCount == 1
                ? "Delete this message for everyone in this chat?"
                : $"Delete these {mineCount} messages for everyone in this chat?";
        }
        else if (otherCount > 0 && mineCount == 0)
        {
            prompt = otherCount == 1
                ? "This will be deleted only for you. It will stay on their screen. Delete it?"
                : $"These {otherCount} messages will be deleted only for you. They will stay on their screen. Delete them?";
        }
        else
        {
            prompt = "Your messages will be deleted for everyone. The other messages will be deleted only for you and will stay on their screen. Delete them?";
        }
        if (AppDialog.Show(
                prompt,
                "Delete messages",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var failures = 0;
        foreach (var m in targets)
        {
            try
            {
                if (m.IsFileTransfer && !string.IsNullOrEmpty(m.FileTransferId))
                    await _api.HideFileTransferClientAsync(_currentRoomId, m.FileTransferId);
                else
                    await _api.HideMessageClientAsync(_currentRoomId, m.Id);
                _messages.RemoveAll(x => x.Id == m.Id);
                _selectedMessageIds.Remove(m.Id);
            }
            catch (Exception ex)
            {
                failures++;
                AppLog.Warn($"Delete messages 실패 ({m.Id}): {ex.Message}");
            }
        }

        RenderMessages();
        if (_selectedMessageIds.Count == 0)
            _messageSelectionMode = false;
        UpdateMessageSelectionChrome();
        if (failures > 0)
        {
            AppDialog.Show($"{failures} message(s) could not be deleted.", "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BeginReply(ChatMessage message)
    {
        if (message.IsFileTransfer) return;
        ClearEdit();
        _replyTo = message;
        ReplyPreviewTitle.Text = "Reply";
        ReplyPreviewText.Text = ChatHistoryClipboard.FormatReplyQuote(message, _me.login_id, PeerLoginId());
        ReplyPreviewPanel.Visibility = Visibility.Visible;
        ComposeBox.Focus();
    }

    private void BeginEdit(ChatMessage message)
    {
        if (!CanEditMessage(message)) return;
        ClearReply();
        _editingMessage = message;
        ReplyPreviewTitle.Text = "Edit message";
        var preview = ComposeInputHelper.NormalizeNewlines(message.PlainText ?? "");
        ReplyPreviewText.Text = preview.Length > 120 ? preview[..117] + "..." : preview;
        ReplyPreviewPanel.Visibility = Visibility.Visible;
        ComposeInputHelper.SetPlainText(ComposeBox, message.PlainText ?? "");
        UpdateComposePlaceholder();
        ComposeBox.Focus();
    }

    private static bool CanEditMessage(ChatMessage message) =>
        !message.IsFileTransfer
        && message.IsMine
        && !ChatPlaintext.Display(message.Body).StartsWith("[", StringComparison.Ordinal)
        && !(message.Body ?? "").TrimStart().StartsWith("E1:", StringComparison.Ordinal);

    private void OnCancelReply(object sender, RoutedEventArgs e) => ClearComposeMode();

    private void ClearReply()
    {
        _replyTo = null;
        if (_editingMessage == null)
        {
            ReplyPreviewPanel.Visibility = Visibility.Collapsed;
            ReplyPreviewText.Text = "";
        }
    }

    private void ClearEdit()
    {
        _editingMessage = null;
        if (_replyTo == null)
        {
            ReplyPreviewPanel.Visibility = Visibility.Collapsed;
            ReplyPreviewText.Text = "";
        }
    }

    private void ClearComposeMode()
    {
        _replyTo = null;
        _editingMessage = null;
        ReplyPreviewPanel.Visibility = Visibility.Collapsed;
        ReplyPreviewText.Text = "";
    }

    private async Task AcceptIncomingFileAsync(ChatMessage m, string savePath)
    {
        if (string.IsNullOrEmpty(m.FileTransferId)) return;
        _recipientSavePaths[m.FileTransferId] = savePath;
        try
        {
            if (!await _api.TryRespondFileTransferAsync(m.FileTransferId, true))
                return;
            m.FileStatus = "accepted";
            RenderMessages();
            if (_pendingFilePunches.TryGetValue(m.FileTransferId, out var pending))
            {
                _pendingFilePunches.Remove(m.FileTransferId);
                await OnFilePunch(pending);
            }
            else
            {
                var room = TransferRoomId(m.FileTransferId) ?? _currentRoomId;
                if (room != null)
                {
                    await _ws.RequestFilePunchAsync(room, m.FileTransferId);
                    SchedulePunchWatch(m.FileTransferId, room);
                }
            }
        }
        catch (Exception ex)
        {
            AppDialog.Show(UserErrorMessages.ForUi(ex, "accept file"), "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnFileBubbleRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: ChatMessage m } || !m.IsFileTransfer) return;
        e.Handled = true;
        if (sender is FrameworkElement fe)
            ShowFileTransferContextMenu(m, fe);
    }

    private void ShowFileTransferContextMenu(ChatMessage m, FrameworkElement placementTarget)
    {
        var menu = new ContextMenu { PlacementTarget = placementTarget };
        var hasImage = TryGetImagePath(m, out var path);
        if (hasImage)
        {
            var copy = new MenuItem { Header = "Copy image" };
            copy.Click += (_, _) => CopyImageFileToClipboard(path);
            menu.Items.Add(copy);

            var save = new MenuItem { Header = "Save as" };
            save.Click += (_, _) => SaveImageFileAs(path, m);
            menu.Items.Add(save);

            var open = new MenuItem { Header = "View full size" };
            open.Click += (_, _) => new ImagePreviewWindow(path, this).ShowDialog();
            menu.Items.Add(open);
        }
        if (m.FileStatus == "completed" && !m.IsSnapshot)
        {
            var show = new MenuItem { Header = "Show in folder" };
            show.Click += (_, _) => ShowTransferInExplorer(m);
            menu.Items.Add(show);
        }

        if (!m.IsMine && m.FileStatus == "pending")
        {
            var save = new MenuItem { Header = "Save" };
            save.Click += async (_, _) => await SaveIncomingTransferAsync(m);
            menu.Items.Add(save);
        }

        if (CanCancelFileTransfer(m))
        {
            var cancel = new MenuItem { Header = "Cancel" };
            cancel.Click += async (_, _) => await CancelFileTransferAsync(m);
            menu.Items.Add(cancel);
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new Separator());
        var delete = new MenuItem { Header = "Delete" };
        delete.SetResourceReference(StyleProperty, "DangerMenuItemStyle");
        delete.Click += async (_, _) => await DeleteFileTransferAsync(m);
        menu.Items.Add(delete);
        placementTarget.ContextMenu = menu;
        menu.IsOpen = true;
    }

    private static bool CanCancelFileTransfer(ChatMessage m) =>
        m.IsFileTransfer
        && !string.IsNullOrEmpty(m.FileTransferId)
        && m.FileStatus is "pending" or "accepted" or "in_progress" or "paused";

    private async Task CancelFileTransferAsync(ChatMessage m)
    {
        if (!CanCancelFileTransfer(m))
            return;
        if (AppDialog.Show(
                "Cancel this file transfer?",
                "Cancel transfer",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        if (!await StopTransferOnServerAsync(m))
        {
            AppDialog.Show("Could not cancel the transfer on the server.", "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        m.FileStatus = "cancelled";
        RenderMessages();
    }

    private async Task DeleteFileTransferAsync(ChatMessage m)
    {
        if (_currentRoomId == null || string.IsNullOrEmpty(m.FileTransferId))
            return;
        var active = CanCancelFileTransfer(m);
        var question = active
            ? "The transfer will be stopped. Do you want to continue?"
            : "Delete this item from the chat history for both sides?";
        if (AppDialog.Show(question, "Delete", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
            return;

        var transferId = m.FileTransferId!;
        if (active && !await StopTransferOnServerAsync(m))
        {
            AppDialog.Show("Could not cancel the transfer on the server.", "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            await _api.HideFileTransferClientAsync(ChatIds.NormalizeRoomId(_currentRoomId), transferId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"파일 전송 이력 숨김 실패 ({transferId}): {ex.Message}");
            AppDialog.Show("Could not remove the transfer from history.", "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _messages.RemoveAll(x => x.FileTransferId == transferId);
        _selectedMessageIds.Remove($"file:{transferId}");
        CleanupSenderTransfer(transferId);
        _recipientSavePaths.Remove(transferId);
        _transferStore.Remove(transferId);
        RenderMessages();
    }

    /// <summary>Stops local sockets, cancels on the server, and removes half-written files.</summary>
    private async Task<bool> StopTransferOnServerAsync(ChatMessage m)
    {
        var transferId = m.FileTransferId!;
        _userCancelledTransfers.Add(transferId);
        AbortLocalFileTransfer(transferId);
        try
        {
            await _api.CancelFileTransferAsync(transferId);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"파일 Cancel transfer API 실패 ({transferId}): {ex.Message}");
            return false;
        }

        if (!m.IsMine)
            DeletePartialReceivedFiles(transferId);
        return true;
    }

    private void DeletePartialReceivedFiles(string transferId)
    {
        var state = _transferStore.GetReceiver(transferId);
        if (state == null)
            return;
        for (var i = 0; i < state.TargetPaths.Count && i < state.Sizes.Count; i++)
        {
            try
            {
                var info = new FileInfo(state.TargetPaths[i]);
                if (info.Exists && info.Length < state.Sizes[i])
                    info.Delete();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"부분 수신 파일 삭제 실패: {ex.Message}");
            }
        }
    }

    private void AbortLocalFileTransfer(string transferId)
    {
        if (_senderTransferCts.Remove(transferId, out var sendCts))
        {
            try
            {
                sendCts.Cancel();
            }
            catch
            {
                // ignore
            }

            sendCts.Dispose();
        }

        if (_recipientReceiveCts.Remove(transferId, out var recvCts))
        {
            try
            {
                recvCts.Cancel();
            }
            catch
            {
                // ignore
            }

            recvCts.Dispose();
        }

        _senderTransferStarted.Remove(transferId);
    }

    private bool TryGetImagePath(ChatMessage m, out string path)
    {
        path = m.LocalImagePath ?? "";
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
            return true;
        if (!string.IsNullOrEmpty(m.FileTransferId)
            && _transferLocalPaths.TryGetValue(m.FileTransferId, out var known)
            && File.Exists(known))
        {
            path = known;
            return true;
        }
        path = "";
        return false;
    }

    private static void CopyImageFileToClipboard(string path)
    {
        try
        {
            var img = ScreenCaptureService.TryLoadImage(path);
            if (img != null)
                Clipboard.SetImage(img);
        }
        catch (Exception ex)
        {
            AppDialog.Show($"Copy failed: {ex.Message}", "TS3 Messenger", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string SaveAsSuggestedFileName(ChatMessage m)
    {
        if (m.IsSnapshot && !m.IsMine)
            return SnapshotPaths.ReceivedSaveAsFileName(m.SentAt);
        return Path.GetFileName(m.FileName);
    }

    private void SaveImageFileAs(string sourcePath, ChatMessage m)
    {
        var suggested = SaveAsSuggestedFileName(m);
        var dlg = new SaveFileDialog { FileName = suggested, Filter = "PNG|*.png|JPEG|*.jpg;*.jpeg|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.Copy(sourcePath, dlg.FileName, overwrite: true);
        }
        catch (Exception ex)
        {
            AppDialog.Show(UserErrorMessages.ForUi(ex, "save"), "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string DeliveryLabel(ChatMessage m) =>
        m.DeliveryState switch
        {
            MessageDeliveryState.Pending => "✓",
            MessageDeliveryState.Sent => "✓",
            MessageDeliveryState.Delivered => "✓",
            MessageDeliveryState.Read => "✓✓",
            _ => "",
        };

    private async void OnSend(object sender, RoutedEventArgs e) => await SendCurrentAsync();

    private async Task SendCurrentAsync()
    {
        if (_currentRoomId == null || _selected == null) return;
        var text = ComposeInputHelper.GetMessageText(ComposeBox);
        if (text.Length == 0)
            return;

        if (text.Length > 0)
        {
            if (_editingMessage != null)
            {
                try
                {
                    var saved = await _api.PatchMessageAsync(_currentRoomId!, _editingMessage.Id, text);
                    ApplyMessageEdit(saved);
                    ComposeInputHelper.Clear(ComposeBox);
                    ClearComposeMode();
                }
                catch (Exception ex)
                {
                    AppDialog.Show(
                        UserErrorMessages.ForUi(ex, "edit message"),
                        "Edit failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return;
            }

            var clientId = Guid.NewGuid().ToString("N");
            var body = text;
            if (_replyTo != null)
            {
                body = ChatHistoryClipboard.FormatReplyQuote(_replyTo, _me.login_id, PeerLoginId())
                       + "\n\n"
                       + text;
                ClearReply();
            }

            try
            {
                var saved = await _api.PostMessageAsync(_currentRoomId, body, clientId);
                AddMessageIfNew(saved, body, true);
                ComposeInputHelper.Clear(ComposeBox);
            }
            catch (Exception ex)
            {
                AppDialog.Show(
                    UserErrorMessages.ForUi(ex, "send message"),
                    "Send failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }
    }

    private void AddMessageIfNew(Ts3ApiClient.JsonMsg row, string plainText, bool isMine)
    {
        if (_messages.Any(m => m.Id == row.id)) return;
        if (isMine)
            _forceScrollToEnd = true;
        _messages.Add(new ChatMessage
        {
            Id = row.id,
            RoomId = row.room_id,
            SenderUserId = row.sender_user_id,
            Body = row.body,
            PlainText = plainText,
            SentAt = ChatTimestamps.NormalizeUtc(row.sent_at),
            IsMine = isMine,
            DeliveryState = isMine ? MessageDeliveryState.Sent : MessageDeliveryState.Delivered,
        });
        SetLocalContactPreview(row.room_id, plainText, ChatTimestamps.NormalizeUtc(row.sent_at), isMine);
        RenderMessages();
    }

    private void SetLocalContactPreview(string roomId, string plainText, DateTime at, bool mine)
    {
        var norm = ChatIds.NormalizeRoomId(roomId);
        var contact = _contacts.FirstOrDefault(c =>
            !string.IsNullOrEmpty(c.DirectRoomId) && ChatIds.NormalizeRoomId(c.DirectRoomId) == norm);
        if (contact == null)
            return;
        contact.SetPreview(NotificationPreview(plainText), at, mine);
        BringContactToTop(contact);
    }

    private async void OnSnapshotButtonClick(object sender, RoutedEventArgs e)
    {
        if (_currentRoomId == null)
        {
            AppDialog.Show("Select a chat before sending a snapshot.", "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = await CaptureSnapshotFileAsync();
        if (!string.IsNullOrEmpty(path))
            await SendSnapshotFileAsync(path);
    }

    private async Task<string?> CaptureSnapshotFileAsync()
    {
        EmojiPopup.IsOpen = false;
        var priorState = WindowState;
        Hide();
        await Task.Delay(120);
        RegionCaptureWindow? capture = null;
        try
        {
            capture = new RegionCaptureWindow();
            if (capture.ShowDialog() != true || capture.SelectedRegionScreenPixels is not { } screen)
                return null;

            var outputPath = SnapshotPaths.CreateTempCapturePath();
            ScreenCaptureService.SaveRegionAsPng(screen.X, screen.Y, screen.Width, screen.Height, outputPath);

            Show();
            WindowState = priorState;
            Activate();

            var editor = new SnapshotAnnotateWindow(outputPath) { Owner = this };
            if (editor.ShowDialog() == true && !string.IsNullOrEmpty(editor.ResultPath))
                return editor.ResultPath;
            return null;
        }
        catch (Exception ex)
        {
            AppDialog.Show(UserErrorMessages.ForUi(ex, "snapshot"), "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            Show();
            WindowState = priorState;
            Activate();
            capture?.Close();
        }
    }

    private async Task SendSnapshotFileAsync(string capturePath, string? roomId = null)
    {
        var room = roomId ?? _currentRoomId;
        if (string.IsNullOrEmpty(room)) return;
        var info = new FileInfo(capturePath);
        if (!info.Exists)
            return;
        if (info.Length > FileTransferLimits.MaxFileSizeBytes)
        {
            AppDialog.Show($"Snapshot is too large (max {FileTransferLimits.MaxFileSizeLabel})",
                "TS3 Messenger", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var doc = await _api.CreateFileTransferAsync(
                room,
                info.Name,
                info.Length,
                SnapshotTransferHelper.MimeType);
            var transferId = doc.GetProperty("id").GetString()!;
            _transferRoomIds[transferId] = ChatIds.NormalizeRoomId(room);
            var persisted = SnapshotPaths.TryPersistTransferFile(transferId, info.Name, capturePath);
            var sendPath = persisted ?? capturePath;
            _senderFilePaths[transferId] = sendPath;
            _transferLocalPaths[transferId] = sendPath;
            _ephemeralSendPayloads[transferId] = new FileSendPayload
            {
                LocalPath = sendPath,
                TransferFileName = info.Name,
                Size = info.Length,
                DeleteWhenDone = persisted == null,
            };
            _transferStore.SetSender(new SenderTransferState
            {
                TransferId = transferId,
                LocalPaths = [sendPath],
                Sizes = [info.Length],
            });
            if (ChatIds.NormalizeRoomId(room) == ChatIds.NormalizeRoomId(_currentRoomId ?? ""))
                UpsertFileMessage(doc, "pending", localImagePath: sendPath);
            else
                SetLocalContactPreview(room, "📷 Snapshot", DateTime.UtcNow, true);
        }
        catch (Exception ex)
        {
            if (roomId == null)
            {
                AppDialog.Show(UserErrorMessages.ForUi(ex, "snapshot send"), "TS3 Messenger",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            throw;
        }
    }

    private async void OnPlusButtonClick(object sender, RoutedEventArgs e)
    {
        if (_currentRoomId == null)
        {
            AppDialog.Show("Select a chat before attaching files.", "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var picker = new Windows.FileFolderPickerWindow { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedPaths.Count == 0)
            return;

        await PromptAndSendFilesFromPathsAsync(picker.SelectedPaths);
    }

    private void OnEmojiPickerToggle(object sender, RoutedEventArgs e)
    {
        EmojiPopup.IsOpen = !EmojiPopup.IsOpen;
    }

    private void OnEmojiPicked(object? sender, string emoji)
    {
        InsertAtCaret(emoji);
        ComposeBox.Focus();
    }

    private void OnComposePasting(object sender, DataObjectPastingEventArgs e)
    {
        // Rich clipboard data (RTF from Ctrl+C) bakes in black text. Keep the characters
        // and let the compose box use TextPrimaryBrush.
        if (ReadClipboardText(e.DataObject) is not { Length: > 0 } text)
            return;
        e.CancelCommand();
        var box = (System.Windows.Controls.RichTextBox)ComposeBox;
        var selection = box.Selection;
        selection.Text = text;
        selection.ClearAllProperties();
        box.CaretPosition = selection.End;
    }

    private static string? ReadClipboardText(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.UnicodeText))
            return data.GetData(DataFormats.UnicodeText) as string;
        if (data.GetDataPresent(DataFormats.Text))
            return data.GetData(DataFormats.Text) as string;
        return null;
    }

    private void InsertAtCaret(string text)
    {
        var selection = ((System.Windows.Controls.RichTextBox)ComposeBox).Selection;
        selection.Text = text;
        ComposeBox.CaretPosition = selection.End;
    }

    private async Task PromptAndSendFilesFromPathsAsync(IEnumerable<string> paths)
    {
        if (_currentRoomId == null) return;
        OutgoingBundle bundle;
        try
        {
            bundle = await FileSendPlanner.FromPathsAsync(paths);
        }
        catch (FileSendPlanException ex)
        {
            AppDialog.Show(ex.Message, "TS3 Messenger", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        catch (Exception ex)
        {
            AppDialog.Show(UserErrorMessages.ForUi(ex, "file preparation"), "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (bundle.TotalSize > FileTransferLimits.MaxFileSizeBytes)
        {
            AppDialog.Show($"Too large to send (max {FileTransferLimits.MaxFileSizeLabel} in total).",
                "TS3 Messenger", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = new FileSendConfirmWindow(bundle) { Owner = this };
        if (confirm.ShowDialog() != true)
            return;

        try
        {
            await SendBundleAsync(bundle, confirm.Caption);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"파일 Send failed ({bundle.DisplayName}): {ex.Message}");
            AppDialog.Show(UserErrorMessages.ForUi(ex, "file transfer"), "TS3 Messenger",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task SendBundleAsync(OutgoingBundle bundle, string? caption, string? roomId = null)
    {
        var room = roomId ?? _currentRoomId;
        if (string.IsNullOrEmpty(room))
            return;

        var single = bundle.Items.Count == 1 && !bundle.IsFolder ? bundle.Items[0] : null;
        var isSnapshot = single != null && SnapshotTransferHelper.IsSnapshotFileName(bundle.DisplayName);
        var doc = await _api.CreateFileTransferAsync(
            room,
            bundle.DisplayName,
            bundle.TotalSize,
            isSnapshot ? SnapshotTransferHelper.MimeType : null,
            caption,
            bundle.IsFolder,
            bundle.Items.Select(i => new TransferItem { Path = i.RelativePath, Size = i.Size }).ToList());
        var transferId = doc.GetProperty("id").GetString()!;

        _transferRoomIds[transferId] = ChatIds.NormalizeRoomId(room);
        _transferStore.SetSender(new SenderTransferState
        {
            TransferId = transferId,
            LocalPaths = bundle.Items.Select(i => i.LocalPath).ToList(),
            Sizes = bundle.Items.Select(i => i.Size).ToList(),
        });
        string? previewPath = null;
        if (single != null)
        {
            _senderFilePaths[transferId] = single.LocalPath;
            if (SnapshotTransferHelper.IsImageTransfer(bundle.DisplayName, null, isSnapshot))
            {
                previewPath = single.LocalPath;
                _transferLocalPaths[transferId] = single.LocalPath;
            }
        }

        if (ChatIds.NormalizeRoomId(room) == ChatIds.NormalizeRoomId(_currentRoomId ?? ""))
            UpsertFileMessage(doc, "pending", localImagePath: previewPath);
        else
        {
            var label = isSnapshot
                ? "📷 Snapshot"
                : FileTransferLabels.Title(bundle.DisplayName, bundle.Items.Count, bundle.IsFolder);
            SetLocalContactPreview(room, label, DateTime.UtcNow, true);
        }
    }

    private void OnFileProgress(FileProgress p)
    {
        Dispatcher.Invoke(() =>
        {
            var fileMsg = _messages.FirstOrDefault(m =>
                m.IsFileTransfer && m.FileTransferId == p.TransferId);
            if (fileMsg != null)
            {
                if (fileMsg.FileStatus is "paused" or "cancelled" or "completed" or "failed")
                    return;
                fileMsg.FilePercent = p.Percent;
                fileMsg.BytesDone = p.SentBytes;
                if (fileMsg.FileStatus is "pending" or "accepted")
                    fileMsg.FileStatus = "in_progress";
                if (_transferViews.TryGetValue(p.TransferId, out var view) && view.Message == fileMsg)
                    UpdateTransferView(view);
                else
                    RenderMessages();
                return;
            }
            FileProgressRing.DataContext = p;
            FileProgressText.Text = $"{p.FileName} — {p.Label} ({p.Percent:0}%)";
            FileProgressPanel.Visibility = Visibility.Visible;
            if (p.Percent >= 100)
                FileProgressPanel.Visibility = Visibility.Collapsed;
        });
    }

    private void HandleWsEvent(JsonElement ev)
    {
        var type = ev.GetProperty("type").GetString();
        _ = Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                switch (type)
                {
                    case "presence":
                    case "group_added":
                        await RefreshContactsAsync();
                        break;
                    case "group_deleted":
                        if (ev.TryGetProperty("room_id", out var deletedRoom))
                            RemoveGroupLocally(deletedRoom.GetString() ?? "");
                        break;
                    case "message":
                        var msg = ev.GetProperty("message");
                        await OnIncomingMessage(msg);
                        break;
                    case "delivered":
                        ApplyPeerReceipt(ev, MessageDeliveryState.Delivered);
                        break;
                    case "read":
                        ApplyPeerReceipt(ev, MessageDeliveryState.Read);
                        break;
                    case "file_transfer":
                        await OnFileTransferEvent(ev.GetProperty("transfer"));
                        break;
                    case "file_punch":
                        await OnFilePunch(ev);
                        break;
                    case "file_punch_request":
                        if (ev.TryGetProperty("transfer_id", out var tidEl))
                            await TryResumeOutgoingTransferAsync(tidEl.GetString()!);
                        break;
                    case "history_cleared":
                        OnHistoryCleared(ev);
                        break;
                    case "message_hidden":
                        OnMessageHidden(ev);
                        break;
                    case "message_edited":
                        OnMessageEdited(ev);
                        break;
                    case "file_transfer_hidden":
                        OnFileTransferHidden(ev);
                        break;
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("WS 이벤트 처리", ex);
            }
        });
    }

    private void OnHistoryCleared(JsonElement ev)
    {
        var roomId = ChatIds.NormalizeRoomId(ev.GetProperty("room_id").GetString() ?? "");
        if (roomId != ChatIds.NormalizeRoomId(_currentRoomId ?? "")) return;
        _messages.Clear();
        _senderFilePaths.Clear();
        _recipientSavePaths.Clear();
        _senderTransferStarted.Clear();
        RenderMessages();
    }

    private void OnMessageHidden(JsonElement ev)
    {
        var roomId = ChatIds.NormalizeRoomId(ev.GetProperty("room_id").GetString() ?? "");
        if (roomId != ChatIds.NormalizeRoomId(_currentRoomId ?? "")) return;
        var messageId = ev.GetProperty("message_id").GetString();
        if (string.IsNullOrEmpty(messageId)) return;
        _messages.RemoveAll(m => m.Id == messageId);
        RenderMessages();
    }

    private void OnMessageEdited(JsonElement ev)
    {
        if (!ev.TryGetProperty("message", out var msgEl)) return;
        var roomId = ChatIds.NormalizeRoomId(msgEl.GetProperty("room_id").GetString() ?? "");
        if (roomId != ChatIds.NormalizeRoomId(_currentRoomId ?? "")) return;
        var id = msgEl.GetProperty("id").GetString();
        if (string.IsNullOrEmpty(id)) return;
        var body = msgEl.GetProperty("body").GetString() ?? "";
        DateTime? editedAt = null;
        if (msgEl.TryGetProperty("edited_at", out var editedEl)
            && editedEl.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(editedEl.GetString()))
        {
            editedAt = ChatTimestamps.ParseJsonInstant(editedEl);
        }

        var existing = _messages.FirstOrDefault(m => m.Id == id);
        if (existing == null) return;
        existing.Body = body;
        existing.PlainText = ChatPlaintext.Display(body);
        existing.EditedAt = editedAt;
        RenderMessages();
        _ = RefreshContactsAsync();
    }

    private void ApplyMessageEdit(Ts3ApiClient.JsonMsg saved)
    {
        var existing = _messages.FirstOrDefault(m => m.Id == saved.id);
        if (existing == null) return;
        existing.Body = saved.body;
        existing.PlainText = ChatPlaintext.Display(saved.body);
        existing.EditedAt = saved.edited_at.HasValue
            ? ChatTimestamps.NormalizeUtc(saved.edited_at.Value)
            : DateTime.UtcNow;
        RenderMessages();
        SetLocalContactPreview(_currentRoomId!, existing.PlainText, existing.SentAt, mine: true);
        _ = RefreshContactsAsync();
    }

    private void OnFileTransferHidden(JsonElement ev)
    {
        var roomId = ChatIds.NormalizeRoomId(ev.GetProperty("room_id").GetString() ?? "");
        var transferId = ev.GetProperty("transfer_id").GetString();
        if (string.IsNullOrEmpty(transferId)) return;
        AbortLocalFileTransfer(transferId);
        var completed = _transferViewerStatus.GetValueOrDefault(transferId) == "completed"
                        || _messages.Any(m => m.FileTransferId == transferId && m.FileStatus == "completed");
        if (!completed)
            DeletePartialReceivedFiles(transferId);
        CleanupSenderTransfer(transferId);
        _transferStore.Remove(transferId);
        _recipientSavePaths.Remove(transferId);
        if (roomId != ChatIds.NormalizeRoomId(_currentRoomId ?? "")) return;
        _messages.RemoveAll(m => m.FileTransferId == transferId);
        _selectedMessageIds.Remove($"file:{transferId}");
        RenderMessages();
    }

    private async Task OnIncomingMessage(JsonElement msg)
    {
        var roomId = ChatIds.NormalizeRoomId(msg.GetProperty("room_id").GetString()!);
        var body = msg.GetProperty("body").GetString()!;
        var sender = ChatIds.NormalizeUserId(msg.GetProperty("sender_user_id").GetString()!);
        var plain = ChatPlaintext.Display(body);
        var id = msg.GetProperty("id").GetString()!;
        var isMine = sender == ChatIds.NormalizeUserId(_me.id);
        if (isMine)
            return;

        var inCurrentRoom = roomId == ChatIds.NormalizeRoomId(_currentRoomId ?? "");
        if (inCurrentRoom)
        {
            if (_messages.Any(m => m.Id == id))
                return;
            _messages.Add(new ChatMessage
            {
                Id = id,
                RoomId = roomId,
                SenderUserId = sender,
                Body = body,
                PlainText = plain,
                SentAt = ChatTimestamps.ParseJsonInstant(msg.GetProperty("sent_at")),
                IsMine = false,
                DeliveryState = MessageDeliveryState.Pending,
            });
            SetLocalContactPreview(roomId, plain, _messages[^1].SentAt, mine: false);
            RenderMessages();
        }

        await _ws.MarkDeliveredAsync(roomId, id);
        if (IsChatRoomInView(roomId))
        {
            await _ws.MarkReadAsync(roomId, id);
            await _api.MarkReadAsync(roomId, id);
            ZeroUnreadForRoom(roomId);
        }
        else
        {
            await RefreshContactsAsync();
            var roomContact = FindContactByRoom(roomId);
            if (roomContact?.IsGroup == true)
                ShowIncomingMessageNotification(roomContact.Id, plain, LookupContactLoginId(sender));
            else
                ShowIncomingMessageNotification(sender, plain);
        }
    }

    private ContactVm? FindContactByRoom(string roomId)
    {
        var norm = ChatIds.NormalizeRoomId(roomId);
        return _contacts.FirstOrDefault(c =>
            !string.IsNullOrEmpty(c.DirectRoomId) && ChatIds.NormalizeRoomId(c.DirectRoomId) == norm);
    }

    private void ZeroUnreadForRoom(string roomId)
    {
        var contact = FindContactByRoom(roomId);
        if (contact == null || contact.UnreadCount == 0)
            return;
        contact.UnreadCount = 0;
        UpdateTaskbarBadge();
    }

    private void ShowIncomingMessageNotification(string openId, string plainText, string? title = null)
    {
        var normSender = ChatIds.NormalizeUserId(openId);
        var contact = _contacts.ToArray().FirstOrDefault(c => c.Id.Equals(openId, StringComparison.OrdinalIgnoreCase)
            || ChatIds.NormalizeUserId(c.Id) == normSender);
        var name = title ?? contact?.LoginId ?? normSender;
        TrayApplicationService.Instance.ShowIncomingMessageNotification(
            name,
            NotificationPreview(plainText),
            normSender);
        UpdateTaskbarBadge();
    }

    private static string NotificationPreview(string plain)
    {
        var text = plain.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.Length <= 120)
            return text;
        return text[..117] + "...";
    }

    private void ApplyPeerReceipt(JsonElement ev, MessageDeliveryState state)
    {
        if (!ev.TryGetProperty("user_id", out var userEl))
            return;
        var actorId = ChatIds.NormalizeUserId(userEl.GetString() ?? "");
        if (actorId == ChatIds.NormalizeUserId(_me.id))
            return;
        var messageId = ev.GetProperty("message_id").GetString()!;
        if (state == MessageDeliveryState.Read)
            ApplyPeerReadReceiptUpTo(messageId);
        else
            UpdateDelivery(messageId, state);
    }

    private void ApplyPeerReadReceiptUpTo(string pivotMessageId)
    {
        var pivot = _messages.FirstOrDefault(x => x.Id == pivotMessageId);
        if (pivot == null)
        {
            UpdateDelivery(pivotMessageId, MessageDeliveryState.Read);
            return;
        }

        var pivotAt = pivot.SentAt;
        var roomId = ChatIds.NormalizeRoomId(pivot.RoomId);
        var changed = false;
        foreach (var m in _messages)
        {
            if (!m.IsMine || ChatIds.NormalizeRoomId(m.RoomId) != roomId || m.SentAt > pivotAt)
                continue;
            if (m.DeliveryState >= MessageDeliveryState.Read)
                continue;
            m.DeliveryState = MessageDeliveryState.Read;
            changed = true;
        }

        if (changed)
            RenderMessages();
    }

    private void UpdateDelivery(string messageId, MessageDeliveryState state)
    {
        var m = _messages.FirstOrDefault(x => x.Id == messageId);
        if (m == null || !m.IsMine || state <= m.DeliveryState)
            return;
        m.DeliveryState = state;
        RenderMessages();
    }

    private async Task OnFileTransferEvent(JsonElement transfer)
    {
        var t = TransferJson.Parse(transfer);
        var id = t.Id;
        var senderId = t.SenderUserId;
        var isMine = senderId == ChatIds.NormalizeUserId(_me.id);
        var row = TransferJson.RowForViewer(t, _me.id, isMine);
        var status = ViewerStatus(t, row);
        var fileName = t.FileName;
        var isSnapshot = SnapshotTransferHelper.IsSnapshotFileName(fileName);
        var normRoom = t.RoomId;
        RememberTransferMeta(t);

        var inView = IsChatRoomInView(normRoom);
        if (!isMine && inView)
            await MarkTransferDeliveredIfNeededAsync(id, row);
        if (!isMine && status == "pending" && !inView && row is { Delivered: false } && _notifiedTransfers.Add(id))
        {
            NotifyIncomingFileTransfer(senderId, FileTransferLabels.TitleText(fileName, t.ItemCount, t.IsFolder), isSnapshot);
            if (IsUiRoomActive(normRoom))
                await RefreshContactsAsync();
        }

        if (status is "cancelled" or "paused" or "failed" or "rejected")
            AbortLocalFileTransfer(id);
        if (status is "cancelled" or "rejected")
        {
            if (!isMine)
                DeletePartialReceivedFiles(id);
            CleanupSenderTransfer(id);
            _userCancelledTransfers.Remove(id);
            _transferStore.Remove(id);
        }

        if (IsUiRoomActive(normRoom))
        {
            UpsertFileMessage(transfer);
            if (!isMine && status == "pending" && isSnapshot)
            {
                var msg = _messages.FirstOrDefault(m => m.FileTransferId == id);
                if (msg != null && !_recipientSavePaths.ContainsKey(id))
                {
                    var savePath = SnapshotPaths.ReceivedFilePath(id, fileName);
                    await AcceptIncomingFileAsync(msg, savePath);
                }
            }
        }
        else if (!isMine)
        {
            await EnsureRoomSubscribedAsync(normRoom);
            if (status == "pending" && isSnapshot)
                await AcceptIncomingFileTransferCoreAsync(normRoom, id, fileName, true);
            else if (status is "accepted" or "in_progress" && isSnapshot)
            {
                _recipientSavePaths[id] = SnapshotPaths.ReceivedFilePath(id, fileName);
                await _ws.RequestFilePunchAsync(normRoom, id);
                SchedulePunchWatch(id, normRoom);
            }

            else if (status is "accepted" or "in_progress")
                await EnsureIncomingBundleReceivingAsync(id, normRoom);

            await RefreshContactsAsync();
        }

        if (isMine && status == "accepted")
            await StartSenderTransferAsync(id, fileName);
        else if (!isMine && !isSnapshot && status == "accepted" && IsUiRoomActive(normRoom))
            await EnsureIncomingBundleReceivingAsync(id, normRoom);
        if (isMine && status is "completed" or "failed" or "cancelled" or "rejected")
            CleanupSenderTransfer(id);
    }

    private void UpsertFileMessage(
        JsonElement transfer,
        string? statusOverride = null,
        string? localImagePath = null,
        string? fallbackRoomId = null,
        bool render = true)
    {
        var t = TransferJson.Parse(transfer, fallbackRoomId ?? _currentRoomId);
        if (string.IsNullOrEmpty(t.Id))
            return;
        RememberTransferMeta(t);
        var transferId = t.Id;
        var isMine = t.SenderUserId == ChatIds.NormalizeUserId(_me.id);
        var row = TransferJson.RowForViewer(t, _me.id, isMine);
        var status = statusOverride ?? ViewerStatus(t, row);
        if (status == "expired")
            status = "pending";
        var isSnapshot = SnapshotTransferHelper.IsSnapshotFileName(t.FileName);

        if (!string.IsNullOrEmpty(localImagePath) && File.Exists(localImagePath))
            _transferLocalPaths[transferId] = localImagePath;
        else if (status == "completed" || isMine)
            localImagePath = ResolveStoredImagePath(transferId, t.FileName, status);
        else
            localImagePath = null;
        status = FileTransferViewStatus.WithLocalFileOverride(status, isSnapshot ? localImagePath : null);

        var existing = _messages.FirstOrDefault(m => m.IsFileTransfer && m.FileTransferId == transferId);
        var created = existing == null;
        if (existing == null)
        {
            existing = new ChatMessage
            {
                Id = $"file:{transferId}",
                SentAt = t.CreatedAt ?? ChatTimestamps.NormalizeUtc(DateTime.UtcNow),
                IsFileTransfer = true,
                FileTransferId = transferId,
                FileStatus = status,
            };
            _messages.Add(existing);
            if (isMine && statusOverride == "pending")
                _forceScrollToEnd = true;
        }
        else
            existing.FileStatus = MergeFileTransferStatus(existing.FileStatus, status);

        existing.RoomId = t.RoomId;
        existing.SenderUserId = t.SenderUserId;
        existing.IsMine = isMine;
        existing.FileName = t.FileName;
        existing.FileSizeBytes = t.FileSizeBytes;
        existing.MimeType = t.MimeType;
        existing.IsSnapshot = isSnapshot;
        existing.Caption = t.Caption;
        existing.ItemCount = t.ItemCount;
        existing.IsFolder = t.IsFolder;
        existing.Items = t.Items;
        existing.RecipientUserIds = t.Recipients.Select(r => r.UserId).ToList();
        existing.PeerDelivered = t.Recipients.Any(r => r.Delivered);
        existing.FailureReason = row?.FailureReason;
        if (row != null && row.BytesDone > existing.BytesDone)
            existing.BytesDone = row.BytesDone;
        if (existing.FileStatus == "paused" && existing.FileSizeBytes > 0)
            existing.FilePercent = existing.BytesDone * 100.0 / existing.FileSizeBytes;
        if (!string.IsNullOrEmpty(localImagePath))
            existing.LocalImagePath = localImagePath;

        _messages.Sort((a, b) => a.SentAt.CompareTo(b.SentAt));
        if (created && !string.IsNullOrEmpty(existing.RoomId))
        {
            var normRoom = ChatIds.NormalizeRoomId(existing.RoomId);
            var contact = _contacts.FirstOrDefault(c =>
                !string.IsNullOrEmpty(c.DirectRoomId) && ChatIds.NormalizeRoomId(c.DirectRoomId) == normRoom);
            if (contact == null || contact.PreviewAt is not { } prev || existing.SentAt >= prev)
            {
                var label = isSnapshot ? "📷 Snapshot" : FileTransferLabels.Title(existing);
                SetLocalContactPreview(existing.RoomId, label, existing.SentAt, isMine);
            }
        }
        if (render)
            RenderMessages();
    }

    private static string MergeFileTransferStatus(string current, string incoming)
    {
        if (IsTerminalFileStatus(current) && IsStaleFileStatusDowngrade(current, incoming))
            return current;
        return incoming;
    }

    private static bool IsTerminalFileStatus(string status) =>
        status is "completed" or "failed" or "rejected" or "cancelled";

    private static bool IsStaleFileStatusDowngrade(string current, string incoming) =>
        incoming is "pending" or "accepted" or "in_progress";

    private async Task StartSenderTransferAsync(string transferId, string? fileName)
    {
        var punchRoom = TransferRoomId(transferId);
        if (string.IsNullOrEmpty(punchRoom))
            return;
        var bundle = ResolveSenderBundle(transferId, fileName);
        if (bundle == null)
            return;
        var (paths, sizes) = bundle.Value;
        var problem = FileTransferTcpService.CheckSources(paths, sizes);
        if (problem != null)
        {
            await ReportSourceMissingAsync(transferId, problem);
            return;
        }

        if (!_senderTransferStarted.Add(transferId))
        {
            if (_senderListenEndpoints.TryGetValue(transferId, out var open))
                await _ws.SendFilePunchAsync(punchRoom, transferId, open.Host, open.Port);
            return;
        }

        var port = Random.Shared.Next(52000, 58000);
        var host = FileTransferTcpService.PickLanAddress();
        _senderListenEndpoints[transferId] = (host, port);
        var fileMsg = _messages.FirstOrDefault(m => m.FileTransferId == transferId);
        var displayName = fileMsg != null ? FileTransferLabels.TitleText(fileMsg) : fileName ?? "file";

        var sendCts = new CancellationTokenSource();
        _senderTransferCts[transferId] = sendCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await _fileTcp.SendBundleAsync(paths, sizes, port, transferId, displayName, sendCts.Token);
                Dispatcher.Invoke(() =>
                {
                    if (_userCancelledTransfers.Contains(transferId))
                        return;
                    var m = _messages.FirstOrDefault(x => x.FileTransferId == transferId);
                    if (m == null) return;
                    m.FileStatus = "completed";
                    m.FilePercent = 100;
                    m.BytesDone = m.FileSizeBytes;
                    if (m.IsImageTransfer && paths.Count == 1 && File.Exists(paths[0]))
                    {
                        if (m.IsSnapshot)
                        {
                            var persisted = SnapshotPaths.TryPersistTransferFile(transferId, m.FileName, paths[0]);
                            m.LocalImagePath = persisted ?? paths[0];
                            if (persisted != null)
                                _transferLocalPaths[transferId] = persisted;
                        }
                        else
                            m.LocalImagePath = paths[0];
                    }

                    RenderMessages();
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.Invoke(() =>
                {
                    var m = _messages.FirstOrDefault(x => x.FileTransferId == transferId);
                    if (m != null && _userCancelledTransfers.Contains(transferId))
                    {
                        m.FileStatus = "cancelled";
                        RenderMessages();
                    }
                });
            }
            catch (TransferSourceMissingException ex)
            {
                await Dispatcher.InvokeAsync(() => ReportSourceMissingAsync(transferId, ex.Reason)).Task.Unwrap();
            }
            catch (Exception ex)
            {
                // Network drop or stalled peer: the receiver's punch watch reconnects and resumes.
                AppLog.Warn($"파일 TCP 전송 중단 ({transferId}): {ex.Message}");
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    if (_senderTransferCts.TryGetValue(transferId, out var current) && current == sendCts)
                        _senderTransferCts.Remove(transferId);
                    _senderTransferStarted.Remove(transferId);
                    _senderListenEndpoints.Remove(transferId);
                });
                sendCts.Dispose();
            }
        });

        await Task.Delay(80);
        if (!await _ws.SendFilePunchAsync(punchRoom, transferId, host, port))
            AppLog.Warn($"file_punch Send failed (transfer={transferId})");
    }

    private async Task ReportSourceMissingAsync(string transferId, string reason)
    {
        var m = _messages.FirstOrDefault(x => x.FileTransferId == transferId);
        if (m != null)
        {
            m.FileStatus = "failed";
            m.FailureReason = reason;
            RenderMessages();
        }

        foreach (var recipient in _transferRecipients.GetValueOrDefault(transferId) ?? [])
        {
            try
            {
                await _api.ReportFileTransferFailureAsync(transferId, recipient, reason);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"파일 없음 보고 실패 ({transferId}): {ex.Message}");
            }
        }
    }

    private void CleanupSenderTransfer(string transferId)
    {
        PersistSentSnapshotIfNeeded(transferId);
        _senderFilePaths.Remove(transferId);
        _senderTransferStarted.Remove(transferId);
        _senderListenEndpoints.Remove(transferId);
        if (_ephemeralSendPayloads.Remove(transferId, out var payload))
            payload.Dispose();
    }

    private void PersistSentSnapshotIfNeeded(string transferId)
    {
        var msg = _messages.FirstOrDefault(m => m.FileTransferId == transferId);
        if (msg == null || !msg.IsSnapshot)
            return;

        string? src = null;
        if (_senderFilePaths.TryGetValue(transferId, out var senderPath) && File.Exists(senderPath))
            src = senderPath;
        else if (!string.IsNullOrEmpty(msg.LocalImagePath) && File.Exists(msg.LocalImagePath))
            src = msg.LocalImagePath;

        if (src == null)
            return;

        var dest = SnapshotPaths.TryPersistTransferFile(transferId, msg.FileName, src);
        if (dest == null)
            return;
        _transferLocalPaths[transferId] = dest;
        msg.LocalImagePath = dest;
    }

    private Task OnFilePunch(JsonElement ev) =>
        _fileReceiveCoordinator.RunExclusiveAsync(() => OnFilePunchCoreAsync(ev));

    private async Task OnFilePunchCoreAsync(JsonElement ev)
    {
        var host = ev.GetProperty("host").GetString()!;
        var port = ev.GetProperty("port").GetInt32();
        var transferId = ev.GetProperty("transfer_id").GetString()!;
        if (_recipientReceiveCts.ContainsKey(transferId))
            return;

        List<string> targets;
        List<long> sizes;
        var receiver = _transferStore.GetReceiver(transferId);
        if (receiver != null && receiver.TargetPaths.Count > 0 && receiver.TargetPaths.Count == receiver.Sizes.Count)
        {
            targets = receiver.TargetPaths;
            sizes = receiver.Sizes;
        }
        else if (_recipientSavePaths.TryGetValue(transferId, out var savePath)
                 && !Directory.Exists(savePath)
                 && _transferSizes.GetValueOrDefault(transferId) > 0)
        {
            targets = [savePath];
            sizes = [_transferSizes.GetValueOrDefault(transferId)];
        }
        else
        {
            _pendingFilePunches[transferId] = ev.Clone();
            return;
        }

        var fileMsg = _messages.FirstOrDefault(m => m.FileTransferId == transferId);
        var knownStatus = fileMsg?.FileStatus ?? _transferViewerStatus.GetValueOrDefault(transferId);
        if (knownStatus is "completed" or "cancelled" or "failed" or "paused" or "rejected")
            return;
        if (fileMsg != null)
        {
            fileMsg.FileStatus = "in_progress";
            RenderMessages();
        }

        var displayName = fileMsg != null ? FileTransferLabels.TitleText(fileMsg) : Path.GetFileName(targets[0]);
        var receiveCts = new CancellationTokenSource();
        _recipientReceiveCts[transferId] = receiveCts;
        try
        {
            await _fileTcp.ReceiveBundleAsync(host, port, targets, sizes, transferId, displayName, receiveCts.Token);
            await _api.RecipientCompleteAsync(transferId, true);
            if (targets.Count == 1)
                _transferLocalPaths[transferId] = targets[0];
            _recipientSavePaths.Remove(transferId);
            _pendingFilePunches.Remove(transferId);
            fileMsg = _messages.FirstOrDefault(m => m.FileTransferId == transferId);
            if (fileMsg != null)
            {
                fileMsg.FileStatus = "completed";
                fileMsg.FilePercent = 100;
                fileMsg.BytesDone = fileMsg.FileSizeBytes;
                if (targets.Count == 1)
                    fileMsg.LocalImagePath = targets[0];
                RenderMessages();
            }
        }
        catch (OperationCanceledException)
        {
            if (fileMsg != null && _userCancelledTransfers.Contains(transferId))
            {
                fileMsg.FileStatus = "cancelled";
                RenderMessages();
            }
        }
        catch (TransferSourceMissingException ex)
        {
            if (fileMsg != null)
            {
                fileMsg.FileStatus = "failed";
                fileMsg.FailureReason = ex.Reason;
                RenderMessages();
            }
        }
        catch (TransferLocalWriteException ex)
        {
            AppLog.Warn($"파일 저장 실패 ({transferId}): {ex.Message}");
            try
            {
                await _api.RecipientCompleteAsync(transferId, false, ex.Message);
            }
            catch (Exception apiEx)
            {
                AppLog.Warn($"파일 실패 보고 실패 ({transferId}): {apiEx.Message}");
            }

            if (fileMsg != null)
            {
                fileMsg.FileStatus = "failed";
                RenderMessages();
            }
        }
        catch (Exception ex)
        {
            // Sender offline, network drop, or stalled stream: keep the bytes on disk and retry.
            AppLog.Warn($"파일 수신 중단 ({transferId}): {ex.Message}");
            if (fileMsg != null && fileMsg.FileStatus == "in_progress")
            {
                fileMsg.FileStatus = "accepted";
                RenderMessages();
            }

            if (!_userCancelledTransfers.Contains(transferId))
                SchedulePunchWatch(transferId, TransferRoomId(transferId));
        }
        finally
        {
            if (_recipientReceiveCts.TryGetValue(transferId, out var current) && current == receiveCts)
                _recipientReceiveCts.Remove(transferId);
            receiveCts.Dispose();
        }
    }

    private sealed class ContactVm : INotifyPropertyChanged
    {
        private int _unreadCount;
        private bool _online;
        private string? _lanIp;
        private string _loginId = "";
        private string? _preview;
        private DateTime? _previewAt;
        private bool _previewMine;

        public string Id { get; set; } = "";
        public bool IsGroup { get; init; }
        public bool CanDeleteGroup { get; set; }
        public Visibility PersonMarkVisibility => IsGroup ? Visibility.Collapsed : Visibility.Visible;
        public Visibility GroupMarkVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;

        public string LoginId
        {
            get => _loginId;
            set
            {
                if (_loginId == value) return;
                _loginId = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Initial));
            }
        }

        public bool Online
        {
            get => _online;
            set
            {
                if (_online == value) return;
                _online = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OnlineDotVisibility));
                OnPropertyChanged(nameof(PresenceText));
                OnPropertyChanged(nameof(SubtitleText));
                OnPropertyChanged(nameof(LanIpLabel));
                OnPropertyChanged(nameof(LanIpVisibility));
            }
        }

        public string? LanIp
        {
            get => _lanIp;
            set
            {
                if (_lanIp == value) return;
                _lanIp = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LanIpLabel));
                OnPropertyChanged(nameof(LanIpVisibility));
            }
        }

        public string PresenceText => IsGroup ? "Group" : ContactPresenceFormatter.Format(Online);
        public string LanIpLabel =>
            !IsGroup && Online && !string.IsNullOrWhiteSpace(LanIp) ? $"  ·  {LanIp}" : "";
        public Visibility LanIpVisibility =>
            LanIpLabel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        public string Initial => AvatarPalette.Initial(LoginId);
        public Brush AvatarBrush => AvatarPalette.For(Id);
        public Visibility OnlineDotVisibility => !IsGroup && Online ? Visibility.Visible : Visibility.Collapsed;

        public string SubtitleText =>
            string.IsNullOrEmpty(_preview) ? PresenceText : _previewMine ? $"You: {_preview}" : _preview;

        public DateTime? PreviewAt => _previewAt;
        public string PreviewTime => _previewAt is { } at ? ChatTimestamps.FormatListTime(at) : "";

        public void SetPreview(string? text, DateTime? at, bool mine)
        {
            if (_preview == text && _previewAt == at && _previewMine == mine) return;
            _preview = text;
            _previewAt = at;
            _previewMine = mine;
            OnPropertyChanged(nameof(SubtitleText));
            OnPropertyChanged(nameof(PreviewTime));
        }

        public string? DirectRoomId { get; set; }

        public int UnreadCount
        {
            get => _unreadCount;
            set
            {
                if (_unreadCount == value) return;
                _unreadCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BadgeVisibility));
                OnPropertyChanged(nameof(UnreadBadgeText));
            }
        }

        public string UnreadBadgeText => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
        public Visibility BadgeVisibility => UnreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public static ContactVm ForGroup(RoomItem room)
        {
            var roomId = ChatIds.NormalizeRoomId(room.Id);
            var vm = new ContactVm
            {
                Id = "group:" + roomId,
                IsGroup = true,
                DirectRoomId = roomId,
            };
            vm.LoginId = string.IsNullOrWhiteSpace(room.Name) ? "Group" : room.Name.Trim();
            vm.UnreadCount = room.UnreadCount;
            vm.SetPreview(room.LastMessagePreview, room.LastMessageAt, room.LastMessageMine);
            return vm;
        }

        public static ContactVm From(ContactItem c)
        {
            var vm = new ContactVm { Id = c.Id };
            vm.ApplyFrom(c);
            return vm;
        }

        public void ApplyFrom(ContactItem c)
        {
            LoginId = c.LoginId;
            Online = c.Online;
            LanIp = c.Online ? c.LanIp : null;
            if (!string.IsNullOrEmpty(c.DirectRoomId))
                DirectRoomId = c.DirectRoomId;
            UnreadCount = c.UnreadCount;
            SetPreview(c.LastMessagePreview, c.LastMessageAt, c.LastMessageMine);
        }
    }
}
