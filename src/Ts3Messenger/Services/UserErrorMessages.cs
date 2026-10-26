using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace Ts3Messenger.Services;

public static class UserErrorMessages
{
    public static string ForUi(Exception ex, string context)
    {
        AppLog.Error(context, ex);
        return Map(ex);
    }

    private static string Map(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerException != null)
            return Map(agg.InnerException);

        if (ex is System.Windows.Markup.XamlParseException)
            return "Could not render the UI. Reinstall the latest build.";

        if (ex is JsonException)
            return InvalidServerResponse;

        if (ex is TaskCanceledException or OperationCanceledException)
            return "The server did not respond in time. Check your network and server status.";

        if (ex is UriFormatException)
            return "Invalid server URL. Example: https://127.0.0.1:8443 or http://192.168.0.10";

        if (ex is InvalidOperationException ioe && !string.IsNullOrWhiteSpace(ioe.Message))
            return ioe.Message;

        if (ex is HttpRequestException httpEx)
            return MapHttp(httpEx);

        if (ex.InnerException != null)
            return Map(ex.InnerException);

        return "Could not complete the request. Try again later.";
    }

    private const string InvalidServerResponse =
        "Could not communicate with the server. Check the URL and that TS-3 is running.";

    private static string MapHttp(HttpRequestException ex)
    {
        if (ex.InnerException is SocketException)
            return "Cannot reach the server. Check address, port, firewall, and Docker/proxy.";

        var msg = ex.Message;
        if (msg.Contains("401", StringComparison.Ordinal) || msg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            return "Invalid login ID or password.";
        if (msg.Contains("403", StringComparison.Ordinal))
            return "Access denied.";
        if (msg.Contains("404", StringComparison.Ordinal))
            return "API not found on the server. Check the URL and server version.";
        if (msg.Contains("400", StringComparison.Ordinal))
        {
            if (msg.Contains("File size", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("size not allowed", StringComparison.OrdinalIgnoreCase))
                return $"File is too large. (Server max {FileTransferLimits.MaxFileSizeLabel})";
            if (msg.Contains("No recipients", StringComparison.OrdinalIgnoreCase))
                return "No recipient available. Ensure the peer can use chat.";
            return "Invalid request. Check the file name and size.";
        }

        return InvalidServerResponse;
    }
}
