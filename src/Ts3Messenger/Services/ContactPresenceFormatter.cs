namespace Ts3Messenger.Services;

internal static class ContactPresenceFormatter
{
    public static string Format(bool online) => online ? "online" : "offline";
}
