using System.IO;

namespace Ts3Messenger.Services;

public sealed class FileSendPayload : IDisposable
{
    public required string LocalPath { get; init; }
    public required string TransferFileName { get; init; }
    public long Size { get; init; }
    public bool DeleteWhenDone { get; init; }

    public void Dispose()
    {
        if (!DeleteWhenDone)
            return;
        try
        {
            if (File.Exists(LocalPath))
                File.Delete(LocalPath);
        }
        catch
        {
            // best effort
        }
    }
}

public sealed class OutgoingItem
{
    public required string LocalPath { get; init; }
    public required string RelativePath { get; init; }
    public long Size { get; init; }
}

/// <summary>Everything the user dropped or picked at once; sent and shown as one chat item.</summary>
public sealed class OutgoingBundle
{
    public required string DisplayName { get; init; }
    public bool IsFolder { get; init; }
    public required List<OutgoingItem> Items { get; init; }
    public long TotalSize => Items.Sum(i => i.Size);
}

public sealed class FileSendPlanException(string message) : Exception(message);

public static class FileSendPlanner
{
    public const int MaxItems = 1000;

    public static Task<OutgoingBundle> FromPathsAsync(IEnumerable<string> paths) =>
        Task.Run(() => Plan(paths));

    public static OutgoingBundle Plan(IEnumerable<string> paths)
    {
        var roots = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => File.Exists(p) || Directory.Exists(p))
            .ToList();
        if (roots.Count == 0)
            throw new FileSendPlanException("No files to send.");

        var items = new List<OutgoingItem>();
        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System,
        };

        foreach (var root in roots)
        {
            if (File.Exists(root))
            {
                AddItem(items, usedPaths, root, Path.GetFileName(root));
                continue;
            }

            var dirName = new DirectoryInfo(root).Name;
            foreach (var file in Directory.EnumerateFiles(root, "*", options))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                AddItem(items, usedPaths, file, $"{dirName}/{rel}");
                if (items.Count > MaxItems)
                    throw new FileSendPlanException($"Too many files. You can send up to {MaxItems:N0} files at once.");
            }
        }

        if (items.Count == 0)
            throw new FileSendPlanException("No files to send. Empty folders cannot be sent.");
        if (items.Count > MaxItems)
            throw new FileSendPlanException($"Too many files. You can send up to {MaxItems:N0} files at once.");

        var isFolder = roots.Count == 1 && Directory.Exists(roots[0]);
        return new OutgoingBundle
        {
            DisplayName = isFolder ? new DirectoryInfo(roots[0]).Name : Path.GetFileName(roots[0]),
            IsFolder = isFolder,
            Items = items,
        };
    }

    private static void AddItem(List<OutgoingItem> items, HashSet<string> usedPaths, string localPath, string relativePath)
    {
        var rel = relativePath;
        if (!usedPaths.Add(rel))
        {
            var dir = Path.GetDirectoryName(relativePath)?.Replace('\\', '/');
            var stem = Path.GetFileNameWithoutExtension(relativePath);
            var ext = Path.GetExtension(relativePath);
            for (var n = 1; ; n++)
            {
                var name = $"{stem} ({n}){ext}";
                rel = string.IsNullOrEmpty(dir) ? name : $"{dir}/{name}";
                if (usedPaths.Add(rel))
                    break;
            }
        }

        items.Add(new OutgoingItem
        {
            LocalPath = localPath,
            RelativePath = rel,
            Size = new FileInfo(localPath).Length,
        });
    }
}
