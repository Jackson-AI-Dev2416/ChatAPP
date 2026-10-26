using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ts3Messenger.Models;

namespace Ts3Messenger.Services;

public sealed class Ts3ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions ApiJson = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private string _csrf = "";

    public static Ts3ApiClient Create(string baseUrl, bool trustServerCertificate = true) =>
        new(baseUrl, trustServerCertificate);

    public Ts3ApiClient(string baseUrl, bool trustServerCertificate = true)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = _cookies,
            UseCookies = true,
        };
        if (trustServerCertificate)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        _http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Add("X-TS3-Client", "TS3-Messenger");
    }

    public void RestoreCookies(string sessionValue, string csrfValue)
    {
        var uri = _http.BaseAddress!;
        if (!string.IsNullOrWhiteSpace(sessionValue))
            _cookies.Add(uri, new Cookie("ts3_session", sessionValue, "/"));
        if (!string.IsNullOrWhiteSpace(csrfValue))
        {
            _cookies.Add(uri, new Cookie("ts3_csrf", csrfValue, "/"));
            _csrf = csrfValue;
        }
    }

    public void PersistSessionCookies()
    {
        var uri = _http.BaseAddress!;
        var session = _cookies.GetCookies(uri)["ts3_session"]?.Value ?? "";
        var csrf = _cookies.GetCookies(uri)["ts3_csrf"]?.Value ?? "";
        SessionCookieStore.Save(uri.GetLeftPart(UriPartial.Authority), session, csrf);
    }

    public string CookieHeader
    {
        get
        {
            var uri = _http.BaseAddress!;
            var list = _cookies.GetCookies(uri);
            return string.Join("; ", list.Cast<Cookie>().Select(c => $"{c.Name}={c.Value}"));
        }
    }

    public async Task EnsureCsrfAsync()
    {
        var res = await _http.GetAsync("api/v1/auth/csrf");
        await ThrowIfFailedAsync(res, "CSRF");
        _csrf = _cookies.GetCookies(_http.BaseAddress!)["ts3_csrf"]?.Value ?? "";
    }

    public async Task<UserMe> LoginAsync(string loginId, string password)
    {
        await EnsureCsrfAsync();
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/login");
        req.Headers.Add("X-CSRF-Token", _csrf);
        req.Content = JsonContent.Create(new { login_id = loginId, password });
        var res = await _http.SendAsync(req);
        await ThrowIfFailedAsync(res, "로그인");
        _csrf = _cookies.GetCookies(_http.BaseAddress!)["ts3_csrf"]?.Value ?? _csrf;
        var me = await ReadJsonBodyAsync<UserMe>(res, "로그인 응답");
        PersistSessionCookies();
        return me;
    }

    public async Task<UserMe> MeAsync()
    {
        var res = await _http.GetAsync("api/v1/auth/me");
        await ThrowIfFailedAsync(res, "세션 확인");
        var me = await ReadJsonBodyAsync<UserMe>(res, "세션 확인 응답");
        PersistSessionCookies();
        return me;
    }

    public async Task<string> GetCompanyTimeZoneAsync()
    {
        var row = await _http.GetFromJsonAsync<JsonSystemTime>("api/v1/system/time", ApiJson);
        return string.IsNullOrWhiteSpace(row?.company_timezone) ? "UTC" : row.company_timezone;
    }

    public async Task LogoutAsync()
    {
        if (string.IsNullOrEmpty(_csrf))
            await EnsureCsrfAsync();
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/auth/logout");
        req.Headers.Add("X-CSRF-Token", _csrf);
        var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode && res.StatusCode != System.Net.HttpStatusCode.NoContent)
            await ThrowIfFailedAsync(res, "로그아웃");
        var uri = _http.BaseAddress!;
        foreach (var name in new[] { "ts3_session", "ts3_csrf" })
        {
            var existing = _cookies.GetCookies(uri)[name];
            if (existing != null)
                existing.Expired = true;
        }
        _csrf = "";
        SessionCookieStore.Clear();
    }

    public async Task PutPublicKeyAsync(string publicKeyB64)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, "api/v1/chat/crypto/public-key");
        req.Headers.Add("X-CSRF-Token", _csrf);
        req.Content = JsonContent.Create(new { public_key_b64 = publicKeyB64 });
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    public async Task<List<ContactItem>> ContactsAsync()
    {
        var rows = await _http.GetFromJsonAsync<List<JsonContact>>("api/v1/chat/contacts");
        return rows?.Select(r => new ContactItem
        {
            Id = r.id,
            LoginId = r.login_id,
            DisplayName = r.display_name,
            Online = r.online,
            LanIp = r.lan_ip,
            DirectRoomId = r.direct_room_id,
            UnreadCount = r.unread_count,
            LastActivityAt = r.last_activity_at,
            LastLoginAt = r.last_login_at,
            LastMessagePreview = r.last_message_preview is null ? null : ChatPlaintext.Display(r.last_message_preview),
            LastMessageAt = r.last_message_at,
            LastMessageMine = r.last_message_mine,
        }).ToList() ?? [];
    }

    public async Task<Dictionary<string, string>> PublicKeysAsync()
    {
        var rows = await _http.GetFromJsonAsync<List<JsonPubKey>>("api/v1/chat/crypto/public-keys");
        return rows?.ToDictionary(
            r => ChatIds.NormalizeUserId(r.user_id),
            r => r.public_key_b64) ?? new();
    }

    public async Task<string?> GetUserPublicKeyAsync(string userId)
    {
        var res = await _http.GetAsync($"api/v1/chat/crypto/users/{userId}/public-key");
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await ThrowIfFailedAsync(res, "공개키 조회");
        var row = await res.Content.ReadFromJsonAsync<JsonUserPubKey>();
        return row?.public_key_b64;
    }

    public async Task<JsonMsg> PostMessageAsync(string roomId, string body, string? clientMessageId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"api/v1/chat/rooms/{roomId}/messages");
        req.Headers.Add("X-CSRF-Token", _csrf);
        req.Content = JsonContent.Create(new { body, client_message_id = clientMessageId });
        var res = await _http.SendAsync(req);
        await ThrowIfFailedAsync(res, "메시지 전송");
        return (await res.Content.ReadFromJsonAsync<JsonMsg>())!;
    }

    public async Task<JsonMsg> PatchMessageAsync(string roomId, string messageId, string body)
    {
        var normRoom = ChatIds.NormalizeRoomId(roomId);
        using var req = new HttpRequestMessage(
            HttpMethod.Patch,
            $"api/v1/chat/rooms/{normRoom}/messages/{messageId}");
        req.Headers.Add("X-CSRF-Token", _csrf);
        req.Content = JsonContent.Create(new { body });
        var res = await _http.SendAsync(req);
        await ThrowIfFailedAsync(res, "메시지 수정");
        return (await res.Content.ReadFromJsonAsync<JsonMsg>(ApiJson))!;
    }

    public async Task<List<RoomItem>> RoomsAsync()
    {
        var rows = await _http.GetFromJsonAsync<List<JsonRoom>>("api/v1/chat/rooms");
        return rows?.Select(r => new RoomItem
        {
            Id = r.id,
            RoomType = r.room_type,
            Name = r.name,
            UnreadCount = r.unread_count,
            LastMessagePreview = r.last_message_preview is null ? null : ChatPlaintext.Display(r.last_message_preview),
            LastMessageAt = r.last_message_at,
            LastMessageMine = r.last_message_mine,
            CreatedByUserId = r.created_by_user_id,
        }).ToList() ?? [];
    }

    public async Task DeleteGroupAsync(string roomId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"api/v1/chat/rooms/{ChatIds.NormalizeRoomId(roomId)}");
        req.Headers.Add("X-CSRF-Token", _csrf);
        var res = await _http.SendAsync(req);
        await ThrowIfFailedAsync(res, "그룹 삭제");
    }

    public async Task<List<RoomMemberItem>> RoomMembersAsync(string roomId)
    {
        var rows = await _http.GetFromJsonAsync<List<JsonRoomMember>>(
            $"api/v1/chat/rooms/{ChatIds.NormalizeRoomId(roomId)}/members");
        return rows?.Select(r => new RoomMemberItem
        {
            UserId = r.user_id,
            LoginId = string.IsNullOrWhiteSpace(r.login_id) ? r.display_name : r.login_id,
        }).ToList() ?? [];
    }

    public async Task<(string Id, string Name)> CreateGroupAsync(string name, IReadOnlyList<string> memberIds)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/chat/rooms/group");
        req.Headers.Add("X-CSRF-Token", _csrf);
        req.Content = JsonContent.Create(new { name, member_ids = memberIds });
        var res = await _http.SendAsync(req);
        await ThrowIfFailedAsync(res, "그룹 만들기");
        var doc = await ReadJsonBodyAsync<JsonElement>(res, "그룹 만들기");
        return (doc.GetProperty("id").GetString()!, doc.GetProperty("name").GetString() ?? name);
    }

    public async Task<string> CreateDirectRoomAsync(string otherUserId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/chat/rooms/direct");
        req.Headers.Add("X-CSRF-Token", _csrf);
        req.Content = JsonContent.Create(new { other_user_id = otherUserId });
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        return doc.GetProperty("id").GetString()!;
    }

    public async Task<List<ChatMessage>> LoadMessagesAsync(string roomId, string myUserId)
    {
        var normRoom = ChatIds.NormalizeRoomId(roomId);
        var normMe = ChatIds.NormalizeUserId(myUserId);
        var rows = await _http.GetFromJsonAsync<List<JsonMsg>>(
            $"api/v1/chat/rooms/{normRoom}/messages?limit=80",
            ApiJson);
        var list = new List<ChatMessage>();
        if (rows == null) return list;
        foreach (var r in rows)
        {
            var sender = ChatIds.NormalizeUserId(r.sender_user_id);
            list.Add(new ChatMessage
            {
                Id = r.id,
                RoomId = normRoom,
                SenderUserId = sender,
                Body = r.body,
                SentAt = ChatTimestamps.NormalizeUtc(r.sent_at),
                EditedAt = r.edited_at.HasValue ? ChatTimestamps.NormalizeUtc(r.edited_at.Value) : null,
                PlainText = ChatPlaintext.Display(r.body),
                IsMine = sender == normMe,
                DeliveryState = MapOutboundReceipt(sender == normMe, r.outbound_receipt),
            });
        }
        return list;
    }

    public async Task<List<JsonElement>> LoadRoomFileTransfersAsync(string roomId)
    {
        var normRoom = ChatIds.NormalizeRoomId(roomId);
        var rows = await _http.GetFromJsonAsync<List<JsonElement>>(
            $"api/v1/chat/rooms/{normRoom}/file-transfers",
            ApiJson);
        return rows ?? [];
    }

    public Task<JsonElement> PauseFileTransferAsync(string transferId, long bytesDone) =>
        PostTransferActionAsync(transferId, "pause", new { bytes_done = Math.Max(0, bytesDone) }, "파일 일시정지");

    public Task<JsonElement> ResumeFileTransferAsync(string transferId) =>
        PostTransferActionAsync(transferId, "resume", new { }, "파일 재개");

    public Task<JsonElement> MarkFileTransferDeliveredAsync(string transferId) =>
        PostTransferActionAsync(transferId, "delivered", new { }, "파일 수신 확인");

    /// <summary>Sender-side report that a recipient can never get the bundle (e.g. source deleted).</summary>
    public Task<JsonElement> ReportFileTransferFailureAsync(string transferId, string recipientUserId, string reason) =>
        PostTransferActionAsync(
            transferId,
            "complete",
            new { success = false, recipient_user_id = recipientUserId, reason },
            "파일 실패 보고");

    private async Task<JsonElement> PostTransferActionAsync(string transferId, string action, object body, string step)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"api/v1/file-transfers/{transferId}/{action}");
        req.Headers.Add("X-CSRF-Token", _csrf);
        req.Content = JsonContent.Create(body);
        var res = await _http.SendAsync(req);
        await ThrowIfFailedAsync(res, step);
        return await ReadJsonBodyAsync<JsonElement>(res, step);
    }

    public async Task CancelFileTransferAsync(string transferId)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/file-transfers/{transferId}/cancel");
        req.Headers.Add("X-CSRF-Token", _csrf);
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    public async Task HideFileTransferClientAsync(string roomId, string transferId)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/chat/rooms/{roomId}/file-transfers/{transferId}/hide-client");
        req.Headers.Add("X-CSRF-Token", _csrf);
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    public async Task MarkReadAsync(string roomId, string messageId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"api/v1/chat/rooms/{roomId}/read/{messageId}");
        req.Headers.Add("X-CSRF-Token", _csrf);
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    public async Task<JsonElement> CreateFileTransferAsync(
        string roomId,
        string fileName,
        long size,
        string? mime,
        string? caption = null,
        bool isFolder = false,
        IReadOnlyList<TransferItem>? items = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/file-transfers");
        req.Headers.Add("X-CSRF-Token", _csrf);
        req.Content = JsonContent.Create(new
        {
            room_id = roomId,
            file_name = fileName,
            file_size_bytes = size,
            mime_type = mime,
            caption,
            is_folder = isFolder,
            items = items?.Select(i => new { path = i.Path, size = i.Size }).ToList(),
        });
        var res = await _http.SendAsync(req);
        await ThrowIfFailedAsync(res, "파일 전송 요청");
        return await ReadJsonBodyAsync<JsonElement>(res, "파일 전송 응답");
    }

    public async Task RespondFileTransferAsync(string transferId, bool accept)
    {
        if (!await TryRespondFileTransferAsync(transferId, accept))
            throw new InvalidOperationException("Could not accept the file transfer.");
    }

    /// <summary>
    /// Returns false only on hard failure. True if accepted or already responded with accept.
    /// </summary>
    public async Task<bool> TryRespondFileTransferAsync(string transferId, bool accept)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"api/v1/file-transfers/{transferId}/respond");
        req.Headers.Add("X-CSRF-Token", _csrf);
        req.Content = JsonContent.Create(new { accept });
        var res = await _http.SendAsync(req);
        if (res.IsSuccessStatusCode)
            return true;
        if (res.StatusCode == System.Net.HttpStatusCode.BadRequest && accept)
        {
            var body = await res.Content.ReadAsStringAsync();
            if (body.Contains("Already responded", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        await ThrowIfFailedAsync(res, "파일 수락");
        return false;
    }

    public async Task RecipientCompleteAsync(string transferId, bool success, string? reason = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"api/v1/file-transfers/{transferId}/recipient-complete");
        req.Headers.Add("X-CSRF-Token", _csrf);
        req.Content = JsonContent.Create(new { success, reason });
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    public async Task ClearClientHistoryAsync(string roomId, ChatWebSocketClient? wsFallback = null)
    {
        var normRoom = ChatIds.NormalizeRoomId(roomId);
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/chat/rooms/{normRoom}/clear-client-history");
        req.Headers.Add("X-CSRF-Token", _csrf);
        req.Content = JsonContent.Create(new { });
        var res = await _http.SendAsync(req);
        if (res.IsSuccessStatusCode) return;
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound && wsFallback?.IsConnected == true)
        {
            if (await wsFallback.ClearClientHistoryAsync(normRoom))
                return;
        }
        await ThrowIfFailedAsync(res, "대화 삭제");
    }

    public async Task HideMessageClientAsync(string roomId, string messageId)
    {
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/chat/rooms/{roomId}/messages/{messageId}/hide-client");
        req.Headers.Add("X-CSRF-Token", _csrf);
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();

    private static async Task ThrowIfFailedAsync(HttpResponseMessage res, string step)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync();
        var path = res.RequestMessage?.RequestUri?.PathAndQuery ?? "";
        AppLog.Error($"{step} HTTP {(int)res.StatusCode} {path} body={Truncate(body, 500)}");
        throw new HttpRequestException(
            $"{step} 실패 ({(int)res.StatusCode} {res.StatusCode}) {path}: {body}");
    }

    private static async Task<T> ReadJsonBodyAsync<T>(HttpResponseMessage res, string step)
    {
        var body = await res.Content.ReadAsStringAsync();
        try
        {
            var value = JsonSerializer.Deserialize<T>(body);
            if (value == null)
                throw new JsonException("empty JSON body");
            return value;
        }
        catch (JsonException ex)
        {
            AppLog.Error(
                $"{step}: expected JSON from {res.RequestMessage?.RequestUri} status={(int)res.StatusCode} body={Truncate(body, 500)}");
            throw new JsonException($"{step}: invalid JSON", ex);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private sealed class JsonSystemTime
    {
        public string company_timezone { get; set; } = "UTC";
    }

    private sealed class JsonRoom
    {
        public string id { get; set; } = "";
        public string room_type { get; set; } = "";
        public string? name { get; set; }
        public int unread_count { get; set; }
        public string? last_message_preview { get; set; }
        public DateTime? last_message_at { get; set; }
        public bool last_message_mine { get; set; }
        public string? created_by_user_id { get; set; }
    }

    private sealed class JsonRoomMember
    {
        public string user_id { get; set; } = "";
        public string login_id { get; set; } = "";
        public string display_name { get; set; } = "";
    }

    private sealed class JsonContact
    {
        public string id { get; set; } = "";
        public string login_id { get; set; } = "";
        public string display_name { get; set; } = "";
        public bool online { get; set; }
        public string? lan_ip { get; set; }
        public string? direct_room_id { get; set; }
        public int unread_count { get; set; }
        public DateTime? last_activity_at { get; set; }
        public DateTime? last_login_at { get; set; }
        public string? last_message_preview { get; set; }
        public DateTime? last_message_at { get; set; }
        public bool last_message_mine { get; set; }
    }

    private sealed class JsonPubKey
    {
        public string user_id { get; set; } = "";
        public string public_key_b64 { get; set; } = "";
    }

    private sealed class JsonUserPubKey
    {
        public string user_id { get; set; } = "";
        public string public_key_b64 { get; set; } = "";
    }

    public sealed class JsonMsg
    {
        public string id { get; set; } = "";
        public string room_id { get; set; } = "";
        public string sender_user_id { get; set; } = "";
        public string body { get; set; } = "";
        public DateTime sent_at { get; set; }
        public DateTime? edited_at { get; set; }

        [JsonPropertyName("outbound_receipt")]
        public string? outbound_receipt { get; set; }
    }

    private static MessageDeliveryState MapOutboundReceipt(bool isMine, string? receipt)
    {
        if (!isMine)
            return MessageDeliveryState.Pending;
        return receipt switch
        {
            "read" => MessageDeliveryState.Read,
            "delivered" => MessageDeliveryState.Sent,
            _ => MessageDeliveryState.Sent,
        };
    }
}
