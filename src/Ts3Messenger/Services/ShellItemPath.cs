using System.IO;
using Microsoft.WindowsAPICodePack.Shell;

namespace Ts3Messenger.Services;

internal static class ShellItemPath
{
    public static string? TryGetFileSystemPath(ShellObject item)
    {
        try
        {
            if (item is ShellFile file)
                return file.Path;

            if (item is ShellFileSystemFolder folder)
                return folder.Path;

            if (item is FileSystemKnownFolder knownFolder)
                return knownFolder.Path;

            if (item.IsFileSystemObject)
            {
                var displayPath = item.GetDisplayName(DisplayNameType.FileSystemPath);
                if (!string.IsNullOrWhiteSpace(displayPath)
                    && (Directory.Exists(displayPath) || File.Exists(displayPath)))
                    return Path.GetFullPath(displayPath);
            }

            var parsing = item.ParsingName;
            if (string.IsNullOrWhiteSpace(parsing))
                return null;

            if (Directory.Exists(parsing) || File.Exists(parsing))
                return Path.GetFullPath(parsing);
        }
        catch
        {
            // non-filesystem shell items (e.g. This PC children) have no local path
        }

        return null;
    }
}
