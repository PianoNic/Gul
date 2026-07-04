using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Gul.Client;

/// <summary>
/// Maps each translated local origin to a short, stable route id and back. The forward direction
/// is a deterministic hash, but the reverse (id → origin) can't be recomputed, so it's the state
/// worth keeping: it's shared across every <see cref="Translator"/> the client builds (so a
/// reconnect doesn't forget it) and persisted to disk (so a restart doesn't either). Without that,
/// a browser holding already-translated URLs would hit an empty map and get 502s until a hard reload.
/// </summary>
public sealed class RouteTable
{
    private readonly ConcurrentDictionary<string, string> _idToTarget = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _targetToId = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _path;
    private readonly object _saveLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>An in-memory table with no persistence (used by tests and one-off translators).</summary>
    public RouteTable() { }

    private RouteTable(string path) => _path = path;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gul");

    /// <summary>Load (or start) the table persisted for a given primary port.</summary>
    public static RouteTable ForPort(int primaryPort) => Load(Path.Combine(Dir, $"routes-{primaryPort}.json"));

    /// <summary>Load (or start) a table persisted at an explicit path.</summary>
    public static RouteTable Load(string path)
    {
        var table = new RouteTable(path);
        table.LoadFromDisk();
        return table;
    }

    public string GetOrAllocateId(string targetBase)
    {
        if (_targetToId.TryGetValue(targetBase, out var existing)) return existing;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(targetBase))).ToLowerInvariant();
        var id = "r" + hash[..10];
        _idToTarget[id] = targetBase;
        _targetToId[targetBase] = id;
        Save();
        return id;
    }

    public bool TryGetTarget(string id, out string target) => _idToTarget.TryGetValue(id, out target!);

    private void LoadFromDisk()
    {
        if (_path is null || !File.Exists(_path)) return;
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));
            if (map is null) return;
            foreach (var (id, target) in map)
            {
                _idToTarget[id] = target;
                _targetToId[target] = id;
            }
        }
        catch { /* a corrupt or unreadable file just means we relearn the routes lazily */ }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            lock (_saveLock)
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, JsonSerializer.Serialize(new Dictionary<string, string>(_idToTarget), JsonOptions));
            }
        }
        catch { /* persistence is best-effort; the table still works in memory */ }
    }
}
