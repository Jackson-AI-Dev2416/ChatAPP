using System.Collections.Concurrent;

namespace Ts3Messenger.Services;

public sealed class RoomKeyCache
{
    private readonly ConcurrentDictionary<string, byte[]> _keys = new();

    public void Set(string roomId, byte[] key) =>
        _keys[ChatIds.NormalizeRoomId(roomId)] = key;

    public bool TryGet(string roomId, out byte[] key) =>
        _keys.TryGetValue(ChatIds.NormalizeRoomId(roomId), out key!);

    public void Remove(string roomId) =>
        _keys.TryRemove(ChatIds.NormalizeRoomId(roomId), out _);

    public void Clear() => _keys.Clear();
}
