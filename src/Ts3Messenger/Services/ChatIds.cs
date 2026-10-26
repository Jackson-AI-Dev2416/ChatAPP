namespace Ts3Messenger.Services;

public static class ChatIds
{
    public static string NormalizeRoomId(string roomId)
    {
        if (Guid.TryParse(roomId, out var g))
            return g.ToString("D");
        return roomId.Trim();
    }

    public static string NormalizeUserId(string userId)
    {
        if (Guid.TryParse(userId, out var g))
            return g.ToString("D");
        return userId.Trim();
    }
}
