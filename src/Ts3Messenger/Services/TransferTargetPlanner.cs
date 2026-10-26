using System.IO;
using Ts3Messenger.Models;

namespace Ts3Messenger.Services;

/// <summary>Maps bundle items onto the receiver's chosen folder without overwriting anything.</summary>
public static class TransferTargetPlanner
{
    public static List<string> Plan(string folder, IReadOnlyList<TransferItem> items)
    {
        var root = Path.GetFullPath(folder);
        var rootWithSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        var renamedTops = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<string>(items.Count);

        foreach (var item in items)
        {
            var parts = SplitSafe(item.Path);
            var top = parts[0];
            var isDirectoryTop = parts.Length > 1;
            if (!renamedTops.TryGetValue(top, out var newTop))
            {
                newTop = UniqueName(root, top, isDirectoryTop, taken);
                renamedTops[top] = newTop;
                taken.Add(newTop);
            }

            parts[0] = newTop;
            var full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
            if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Unsafe item path: {item.Path}");
            targets.Add(full);
        }

        return targets;
    }

    private static string[] SplitSafe(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        var invalid = Path.GetInvalidFileNameChars();
        if (parts.Length == 0 || Path.IsPathRooted(relativePath))
            throw new InvalidOperationException($"Unsafe item path: {relativePath}");
        foreach (var p in parts)
        {
            if (p.Length == 0 || p == "." || p == ".." || p.IndexOfAny(invalid) >= 0)
                throw new InvalidOperationException($"Unsafe item path: {relativePath}");
        }

        return parts;
    }

    private static string UniqueName(string root, string name, bool isDirectory, HashSet<string> taken)
    {
        bool Exists(string candidate) =>
            taken.Contains(candidate)
            || File.Exists(Path.Combine(root, candidate))
            || Directory.Exists(Path.Combine(root, candidate));

        if (!Exists(name))
            return name;
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);
        var ext = isDirectory ? "" : Path.GetExtension(name);
        for (var n = 1; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!Exists(candidate))
                return candidate;
        }
    }
}
