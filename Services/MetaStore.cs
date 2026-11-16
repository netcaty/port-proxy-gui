using PortProxyManager.Models;

namespace PortProxyManager.Services;

/// <summary>
/// meta.json 的读写。里面的 name / group / note 只用于界面展示，
/// 绝不会拼进任何命令行——即便该文件被低权限用户篡改也无法造成命令注入。
/// </summary>
public sealed class MetaStore
{
    private sealed class Document
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, EntryMeta> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly string _file;
    private readonly Dictionary<string, EntryMeta> _map = new(StringComparer.OrdinalIgnoreCase);

    public MetaStore(string file)
    {
        _file = file;
        Load();
    }

    public int Count => _map.Count;

    private void Load()
    {
        try
        {
            var doc = JsonIo.Read<Document>(_file);
            if (doc?.Entries is null) return;

            foreach (var pair in doc.Entries)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                _map[pair.Key.Trim()] = pair.Value ?? new EntryMeta();
            }
        }
        catch (Exception ex)
        {
            // 文件坏了不能拖死启动：备份坏文件后从空开始
            LogService.Current?.Error($"meta.json 解析失败，已备份为 .broken：{ex.Message}");
            TryQuarantine();
        }
    }

    private void TryQuarantine()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var bad = _file + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Move(_file, bad, overwrite: true);
        }
        catch
        {
            // 隔离失败也不影响主流程
        }
    }

    public EntryMeta? Get(string key) =>
        _map.TryGetValue(key, out var meta) ? meta : null;

    public EntryMeta GetOrCreate(string key)
    {
        if (!_map.TryGetValue(key, out var meta))
        {
            meta = new EntryMeta();
            _map[key] = meta;
        }
        return meta;
    }

    public void Set(string key, EntryMeta meta) => _map[key] = meta;

    public bool Remove(string key) => _map.Remove(key);

    public IReadOnlyList<string> AllKeys => _map.Keys.ToList();

    /// <summary>有备注但注册表里已经没有对应映射的条目（外部删除后留下的孤儿）。</summary>
    public IReadOnlyList<string> FindOrphans(IEnumerable<PortProxyEntry> current)
    {
        var alive = new HashSet<string>(current.Select(e => e.Key), StringComparer.OrdinalIgnoreCase);
        return _map.Keys.Where(k => !alive.Contains(k)).ToList();
    }

    public int RemoveOrphans(IEnumerable<PortProxyEntry> current)
    {
        var orphans = FindOrphans(current);
        foreach (var key in orphans) _map.Remove(key);
        return orphans.Count;
    }

    public void Save()
    {
        var doc = new Document { Entries = new Dictionary<string, EntryMeta>(_map, StringComparer.OrdinalIgnoreCase) };
        JsonIo.Write(_file, doc);
    }
}
