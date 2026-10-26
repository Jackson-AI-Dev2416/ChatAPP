using System.IO;
using System.Text.Json;

namespace Ts3Messenger.Services;

public sealed class SenderTransferState
{
    public string TransferId { get; set; } = "";
    public List<string> LocalPaths { get; set; } = new();
    public List<long> Sizes { get; set; } = new();
}

public sealed class ReceiverTransferState
{
    public string TransferId { get; set; } = "";
    public List<string> TargetPaths { get; set; } = new();
    public List<long> Sizes { get; set; } = new();
}

/// <summary>
/// Per-PC record of which local files belong to which transfer. Transfers never expire,
/// so this must survive app restarts; it never leaves this machine.
/// </summary>
public sealed class TransferStateStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private StoreData _data;

    public TransferStateStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ts3Messenger",
            "transfers.json");
        _data = Load(_path);
    }

    public SenderTransferState? GetSender(string transferId)
    {
        lock (_gate)
            return _data.Senders.GetValueOrDefault(transferId);
    }

    public ReceiverTransferState? GetReceiver(string transferId)
    {
        lock (_gate)
            return _data.Receivers.GetValueOrDefault(transferId);
    }

    public void SetSender(SenderTransferState state)
    {
        lock (_gate)
        {
            _data.Senders[state.TransferId] = state;
            Save();
        }
    }

    public void SetReceiver(ReceiverTransferState state)
    {
        lock (_gate)
        {
            _data.Receivers[state.TransferId] = state;
            Save();
        }
    }

    public void Remove(string transferId)
    {
        lock (_gate)
        {
            var changed = _data.Senders.Remove(transferId) | _data.Receivers.Remove(transferId);
            if (changed)
                Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"transfer state save failed: {ex.Message}");
        }
    }

    private static StoreData Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<StoreData>(File.ReadAllText(path)) ?? new StoreData();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"transfer state load failed: {ex.Message}");
        }

        return new StoreData();
    }

    private sealed class StoreData
    {
        public Dictionary<string, SenderTransferState> Senders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ReceiverTransferState> Receivers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
