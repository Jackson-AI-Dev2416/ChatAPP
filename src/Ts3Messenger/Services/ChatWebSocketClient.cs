using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Ts3Messenger.Services;

public sealed class ChatWebSocketClient : IAsyncDisposable
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Uri? _uri;
    private string? _cookieHeader;
    private int _reconnecting;
    private int _failedReconnects;
    private bool _disposed;

    public event Action<JsonElement>? OnEvent;
    public event Action? OnReconnected;
    public event Action? OnServerLost;

    private const int MaxReconnectFailures = 4;

    public bool IsConnected => _ws is { State: WebSocketState.Open };

    public async Task ConnectAsync(Uri wsUri, string cookieHeader)
    {
        _uri = wsUri;
        _cookieHeader = cookieHeader;
        _cts = new CancellationTokenSource();
        await ConnectOnceAsync();
        _ = Task.Run(ReconnectLoop);
    }

    private async Task ReconnectLoop()
    {
        while (!_disposed)
        {
            try
            {
                await Task.Delay(2500);
                if (_disposed || _uri == null || _cookieHeader == null)
                    continue;
                if (IsConnected)
                {
                    _failedReconnects = 0;
                    continue;
                }
                if (Interlocked.Exchange(ref _reconnecting, 1) == 1)
                    continue;
                try
                {
                    AppLog.Info("채팅 WebSocket 재연결 시도");
                    await ConnectOnceAsync();
                    _failedReconnects = 0;
                    OnReconnected?.Invoke();
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"WebSocket 재연결 실패: {ex.Message}");
                    _failedReconnects++;
                    if (_failedReconnects >= MaxReconnectFailures)
                    {
                        _disposed = true;
                        OnServerLost?.Invoke();
                        return;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _reconnecting, 0);
                }
            }
            catch
            {
                Interlocked.Exchange(ref _reconnecting, 0);
            }
        }
    }

    private async Task ConnectOnceAsync()
    {
        _ws?.Dispose();
        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Cookie", _cookieHeader!);
        _ws.Options.SetRequestHeader("X-TS3-Client", "TS3-Messenger");
        await _ws.ConnectAsync(_uri!, _cts!.Token);
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(PingLoop);
        await SendAsync(new { type = "announce", lan_ip = FileTransferTcpService.PickLanAddress() });
    }

    private async Task PingLoop()
    {
        var socket = _ws;
        while (!_disposed && socket != null && ReferenceEquals(_ws, socket) && socket.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(15000, _cts!.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (!ReferenceEquals(_ws, socket) || socket.State != WebSocketState.Open)
                return;
            await SendAsync(new { type = "ping" });
        }
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[8192];
        try
        {
            while (_ws is { State: WebSocketState.Open })
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, _cts!.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                        return;
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());
                try
                {
                    var doc = JsonDocument.Parse(json);
                    OnEvent?.Invoke(doc.RootElement.Clone());
                }
                catch
                {
                    // ignore malformed
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            AppLog.Warn($"WebSocket 수신 종료: {ex.Message}");
        }
    }

    public async Task SubscribeAsync(string roomId) =>
        await SendAsync(new { type = "subscribe", room_id = roomId });

    public async Task<bool> SendMessageAsync(string roomId, string encryptedBody, string? clientMessageId = null) =>
        await SendAsync(new { type = "send", room_id = roomId, body = encryptedBody, client_message_id = clientMessageId });

    public async Task MarkReadAsync(string roomId, string messageId) =>
        await SendAsync(new { type = "read", room_id = roomId, message_id = messageId });

    public async Task MarkDeliveredAsync(string roomId, string messageId) =>
        await SendAsync(new { type = "delivered", room_id = roomId, message_id = messageId });

    public Task<bool> SendFilePunchAsync(string roomId, string transferId, string host, int port) =>
        SendAsync(new
        {
            type = "file_punch",
            room_id = roomId,
            transfer_id = transferId,
            host,
            port,
        });

    public Task<bool> RequestFilePunchAsync(string roomId, string transferId) =>
        SendAsync(new { type = "file_punch_request", room_id = roomId, transfer_id = transferId });

    public Task<bool> ClearClientHistoryAsync(string roomId) =>
        SendAsync(new { type = "clear_client_history", room_id = roomId });

    private async Task<bool> SendAsync(object payload)
    {
        if (_ws == null || _ws.State != WebSocketState.Open)
            return false;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts!.Token);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"WebSocket 전송 실패: {ex.Message}");
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _cts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            }
            catch
            {
                // ignore
            }
        }

        _ws?.Dispose();
        _ws = null;
        _cts?.Dispose();
        _cts = null;
    }
}
