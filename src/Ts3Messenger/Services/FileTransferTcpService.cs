using System.IO;
using System.Net;
using System.Net.Sockets;
using Ts3Messenger.Models;

namespace Ts3Messenger.Services;

/// <summary>The sender's source file is gone or was modified since it was offered.</summary>
public sealed class TransferSourceMissingException(string reason)
    : Exception($"Source file unavailable ({reason})")
{
    public string Reason { get; } = reason;
}

/// <summary>The receiver could not write to its chosen folder (permissions, disk full, ...).</summary>
public sealed class TransferLocalWriteException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// Peer-to-peer bundle transfer (protocol "TS32"). The receiver connects and states where to
/// resume (file index + byte offset); the sender then streams every remaining byte of the
/// bundle in manifest order. Interrupting the connection is how pause works: the next
/// connection resumes from whatever is already on the receiver's disk.
/// </summary>
public sealed class FileTransferTcpService
{
    private static readonly byte[] Magic = "TS32"u8.ToArray();
    private const byte StatusOk = 0;
    private const byte StatusMissing = 1;
    private const byte StatusChanged = 2;
    private const byte AckComplete = 1;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AcceptTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    public event Action<FileProgress>? Progress;

    public async Task SendBundleAsync(
        IReadOnlyList<string> localPaths,
        IReadOnlyList<long> sizes,
        int port,
        string transferId,
        string displayName,
        CancellationToken ct)
    {
        using var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        TcpClient client;
        try
        {
            using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            acceptCts.CancelAfter(AcceptTimeout);
            client = await listener.AcceptTcpClientAsync(acceptCts.Token);
        }
        finally
        {
            listener.Stop();
        }

        using var _ = client;
        await using var stream = client.GetStream();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            idle.CancelAfter(IdleTimeout);
            var request = new byte[16];
            await ReadExactAsync(stream, request, idle.Token);
            if (!request.AsSpan(0, 4).SequenceEqual(Magic))
                throw new IOException("Unknown transfer protocol");
            var startIndex = BitConverter.ToInt32(request, 4);
            var startOffset = BitConverter.ToInt64(request, 8);
            if (startIndex < 0 || startIndex > sizes.Count
                || startOffset < 0 || (startIndex < sizes.Count && startOffset > sizes[startIndex]))
                throw new IOException("Invalid resume point");

            var problem = CheckSources(localPaths, sizes, startIndex);
            if (problem != null)
            {
                await stream.WriteAsync(new[] { problem == FileTransferLabels.FileNotFoundReason ? StatusMissing : StatusChanged }, idle.Token);
                throw new TransferSourceMissingException(problem);
            }

            await stream.WriteAsync(new[] { StatusOk }, idle.Token);
            var total = sizes.Sum();
            var done = sizes.Take(startIndex).Sum() + startOffset;
            var reporter = new ProgressReporter(this, transferId, displayName, total);
            var buffer = new byte[256 * 1024];
            for (var i = startIndex; i < localPaths.Count; i++)
            {
                var offset = i == startIndex ? startOffset : 0;
                await using var fs = new FileStream(
                    localPaths[i], FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 1, useAsync: true);
                fs.Seek(offset, SeekOrigin.Begin);
                var remaining = sizes[i] - offset;
                while (remaining > 0)
                {
                    var read = await fs.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                    if (read == 0)
                        throw new TransferSourceMissingException(FileTransferLabels.FileChangedReason);
                    idle.CancelAfter(IdleTimeout);
                    await stream.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                    remaining -= read;
                    done += read;
                    reporter.Report(done, force: false);
                }
            }

            reporter.Report(done, force: true);
            idle.CancelAfter(IdleTimeout);
            var ack = new byte[1];
            await ReadExactAsync(stream, ack, idle.Token);
            if (ack[0] != AckComplete)
                throw new IOException("Receiver did not confirm the transfer");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Transfer stalled");
        }
    }

    public async Task ReceiveBundleAsync(
        string host,
        int port,
        IReadOnlyList<string> targetPaths,
        IReadOnlyList<long> sizes,
        string transferId,
        string displayName,
        CancellationToken ct)
    {
        var (startIndex, startOffset) = FindResumePoint(targetPaths, sizes);
        using var client = new TcpClient();
        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(ConnectTimeout);
            try
            {
                await client.ConnectAsync(host, port, connectCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("Could not reach the sender");
            }
        }

        await using var stream = client.GetStream();
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            idle.CancelAfter(IdleTimeout);
            var request = new byte[16];
            Magic.CopyTo(request, 0);
            BitConverter.TryWriteBytes(request.AsSpan(4, 4), startIndex);
            BitConverter.TryWriteBytes(request.AsSpan(8, 8), startOffset);
            await stream.WriteAsync(request, idle.Token);

            var status = new byte[1];
            await ReadExactAsync(stream, status, idle.Token);
            if (status[0] == StatusMissing)
                throw new TransferSourceMissingException(FileTransferLabels.FileNotFoundReason);
            if (status[0] != StatusOk)
                throw new TransferSourceMissingException(FileTransferLabels.FileChangedReason);

            var total = sizes.Sum();
            var done = sizes.Take(startIndex).Sum() + startOffset;
            var reporter = new ProgressReporter(this, transferId, displayName, total);
            var buffer = new byte[256 * 1024];
            for (var i = startIndex; i < targetPaths.Count; i++)
            {
                var offset = i == startIndex ? startOffset : 0;
                await using var fs = OpenTarget(targetPaths[i], offset);
                var remaining = sizes[i] - offset;
                while (remaining > 0)
                {
                    idle.CancelAfter(IdleTimeout);
                    var n = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), idle.Token);
                    if (n == 0)
                        throw new EndOfStreamException("Sender closed the connection");
                    try
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    }
                    catch (IOException ex)
                    {
                        throw new TransferLocalWriteException($"Could not write {targetPaths[i]}", ex);
                    }

                    remaining -= n;
                    done += n;
                    reporter.Report(done, force: false);
                }
            }

            reporter.Report(done, force: true);
            await stream.WriteAsync(new[] { AckComplete }, idle.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Transfer stalled");
        }
    }

    /// <summary>Bytes already on the receiver's disk; used for the pause percentage and resume.</summary>
    public static long BytesOnDisk(IReadOnlyList<string> targetPaths, IReadOnlyList<long> sizes)
    {
        var (index, offset) = FindResumePoint(targetPaths, sizes);
        return sizes.Take(index).Sum() + offset;
    }

    public static string? CheckSources(IReadOnlyList<string> localPaths, IReadOnlyList<long> sizes, int fromIndex = 0)
    {
        for (var i = fromIndex; i < localPaths.Count; i++)
        {
            var info = new FileInfo(localPaths[i]);
            if (!info.Exists)
                return FileTransferLabels.FileNotFoundReason;
            if (info.Length != sizes[i])
                return FileTransferLabels.FileChangedReason;
        }

        return null;
    }

    private static (int Index, long Offset) FindResumePoint(IReadOnlyList<string> targetPaths, IReadOnlyList<long> sizes)
    {
        for (var i = 0; i < targetPaths.Count; i++)
        {
            var info = new FileInfo(targetPaths[i]);
            var length = info.Exists ? info.Length : -1;
            if (length == sizes[i])
                continue;
            return (i, length > 0 && length < sizes[i] ? length : 0);
        }

        return (targetPaths.Count, 0);
    }

    private static FileStream OpenTarget(string path, long offset)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fs = new FileStream(path, offset > 0 ? FileMode.OpenOrCreate : FileMode.Create,
                FileAccess.Write, FileShare.Read, bufferSize: 1, useAsync: true);
            if (offset > 0)
            {
                fs.SetLength(offset);
                fs.Seek(offset, SeekOrigin.Begin);
            }

            return fs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TransferLocalWriteException($"Could not create {path}", ex);
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (n == 0) throw new EndOfStreamException();
            offset += n;
        }
    }

    private sealed class ProgressReporter(FileTransferTcpService owner, string transferId, string name, long total)
    {
        private DateTime _last = DateTime.MinValue;

        public void Report(long done, bool force)
        {
            var now = DateTime.UtcNow;
            if (!force && now - _last < ProgressInterval)
                return;
            _last = now;
            owner.Progress?.Invoke(new FileProgress
            {
                TransferId = transferId,
                FileName = name,
                TotalBytes = total,
                SentBytes = done,
            });
        }
    }

    public static string PickLanAddress()
    {
        var candidates = Dns.GetHostAddresses(Dns.GetHostName())
            .Where(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            .Select(ip => ip.ToString())
            .ToList();
        if (candidates.Count == 0)
            return "127.0.0.1";

        static int Score(string s)
        {
            if (s.StartsWith("192.168.", StringComparison.Ordinal)) return 0;
            if (s.StartsWith("10.", StringComparison.Ordinal)) return 1;
            if (s.StartsWith("172.", StringComparison.Ordinal) && s.Length > 4)
            {
                var parts = s.Split('.');
                if (parts.Length > 1 && int.TryParse(parts[1], out var second) && second is >= 16 and <= 31)
                    return 2;
            }

            return 10;
        }

        return candidates.OrderBy(Score).ThenBy(s => s, StringComparer.Ordinal).First();
    }
}
