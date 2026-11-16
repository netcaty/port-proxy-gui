using PortProxyManager.Models;

namespace PortProxyManager.Services;

public sealed class ExportEntry
{
    public string Family { get; set; } = "v4tov4";
    public string Protocol { get; set; } = "tcp";
    public string ListenAddress { get; set; } = "*";
    public int ListenPort { get; set; }
    public string ConnectAddress { get; set; } = "";
    public int ConnectPort { get; set; }
    public string? Name { get; set; }
    public string? Group { get; set; }
    public string? Note { get; set; }
    public bool HasFirewallRule { get; set; }

    public PortProxyEntry ToEntry() => new()
    {
        Family = Family,
        Protocol = string.IsNullOrWhiteSpace(Protocol) ? "tcp" : Protocol,
        ListenAddress = ListenAddress,
        ListenPort = ListenPort,
        ConnectAddress = ConnectAddress,
        ConnectPort = ConnectPort,
    };

    public EntryMeta ToMeta() => new()
    {
        Name = Name,
        Group = Group,
        Note = Note,
        HasFirewallRule = HasFirewallRule,
        CreatedAt = DateTimeOffset.Now,
    };

    public static ExportEntry From(PortProxyEntry entry, EntryMeta? meta) => new()
    {
        Family = entry.Family,
        Protocol = entry.Protocol,
        ListenAddress = entry.NormalizedListen,
        ListenPort = entry.ListenPort,
        ConnectAddress = entry.NormalizedConnect,
        ConnectPort = entry.ConnectPort,
        Name = meta?.Name,
        Group = meta?.Group,
        Note = meta?.Note,
        HasFirewallRule = meta?.HasFirewallRule ?? false,
    };
}

public sealed class ExportDocument
{
    public int Version { get; set; } = 1;
    public string? ExportedAt { get; set; }
    public string? Host { get; set; }
    public List<ExportEntry> Entries { get; set; } = new();
}

public enum DiffKind
{
    Add,
    Update,
    Delete,
    Same,
}

public sealed record DiffItem(DiffKind Kind, PortProxyEntry Entry, PortProxyEntry? Current, EntryMeta? Meta)
{
    public string KindLabel => Kind switch
    {
        DiffKind.Add => "新增",
        DiffKind.Update => "覆盖",
        DiffKind.Delete => "删除",
        _ => "无变化",
    };
}

public static class BackupService
{
    /// <summary>把当前状态写成导出文档（导入、导出、自动备份共用同一套结构）。</summary>
    public static ExportDocument BuildDocument(
        IEnumerable<PortProxyEntry> entries, MetaStore? meta, string? exportedAt = null)
    {
        var doc = new ExportDocument
        {
            Version = 1,
            ExportedAt = exportedAt ?? DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            Host = Environment.MachineName,
        };

        foreach (var entry in entries)
            doc.Entries.Add(ExportEntry.From(entry, meta?.Get(entry.Key)));

        return doc;
    }

    public static void Export(string file, IEnumerable<PortProxyEntry> entries, MetaStore? meta)
    {
        JsonIo.Write(file, BuildDocument(entries, meta));
        LogService.Current?.Info($"已导出 {entries.Count()} 条映射到 {file}");
    }

    /// <summary>破坏性操作前的自动快照，返回文件路径。</summary>
    public static string? WriteSnapshot(
        PathContext paths, IEnumerable<PortProxyEntry> entries, MetaStore? meta, string tag)
    {
        try
        {
            var safeTag = string.Concat(tag.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
            var file = Path.Combine(paths.BackupDir,
                $"backup-{DateTime.Now:yyyyMMdd-HHmmss}-{safeTag}.json");

            JsonIo.Write(file, BuildDocument(entries, meta));
            LogService.Current?.Info($"已创建快照 {file}");
            return file;
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"创建快照失败：{ex.Message}");
            return null;
        }
    }

    public static ExportDocument LoadDocument(string file)
    {
        var doc = JsonIo.Read<ExportDocument>(file)
                  ?? throw new PortProxyException($"文件不是有效的映射配置：{file}");

        if (doc.Entries is null)
            throw new PortProxyException("文件里没有 entries 字段。");

        return doc;
    }

    /// <summary>对比当前状态与导入文件，得出需要新增/覆盖/删除的条目。</summary>
    public static List<DiffItem> Diff(IEnumerable<PortProxyEntry> current, ExportDocument imported)
    {
        var currentList = current.ToList();
        var currentMap = currentList.ToDictionary(e => e.Key, e => e, StringComparer.OrdinalIgnoreCase);
        var importedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<DiffItem>();

        foreach (var raw in imported.Entries)
        {
            PortProxyEntry entry;
            try
            {
                entry = raw.ToEntry();
            }
            catch (Exception ex)
            {
                LogService.Current?.Warn($"跳过无法解析的条目：{ex.Message}");
                continue;
            }

            importedKeys.Add(entry.Key);

            if (!currentMap.TryGetValue(entry.Key, out var existing))
            {
                items.Add(new DiffItem(DiffKind.Add, entry, null, raw.ToMeta()));
            }
            else if (!existing.SameTarget(entry))
            {
                items.Add(new DiffItem(DiffKind.Update, entry, existing, raw.ToMeta()));
            }
            else
            {
                items.Add(new DiffItem(DiffKind.Same, entry, existing, raw.ToMeta()));
            }
        }

        foreach (var existing in currentList.Where(e => !importedKeys.Contains(e.Key)))
            items.Add(new DiffItem(DiffKind.Delete, existing, existing, null));

        return items
            .OrderBy(i => i.Kind switch
            {
                DiffKind.Delete => 0,
                DiffKind.Update => 1,
                DiffKind.Add => 2,
                _ => 3,
            })
            .ThenBy(i => i.Entry.Family)
            .ThenBy(i => i.Entry.ListenPort)
            .ToList();
    }

    /// <summary>执行 diff 中选中的条目。返回成功数、失败数与错误明细。</summary>
    public static (int Ok, int Failed, List<string> Errors) Apply(
        IEnumerable<DiffItem> selected,
        PortProxyService service,
        MetaStore meta)
    {
        int ok = 0;
        var errors = new List<string>();

        foreach (var item in selected)
        {
            if (item.Kind == DiffKind.Same)
            {
                // 无变化也把备注同步过来
                if (item.Meta is not null) meta.Set(item.Entry.Key, item.Meta);
                ok++;
                continue;
            }

            try
            {
                switch (item.Kind)
                {
                    case DiffKind.Add:
                        service.Add(item.Entry);
                        if (item.Meta is not null) meta.Set(item.Entry.Key, item.Meta);
                        break;

                    case DiffKind.Update:
                        service.Update(item.Current!, item.Entry);
                        if (item.Meta is not null) meta.Set(item.Entry.Key, item.Meta);
                        break;

                    case DiffKind.Delete:
                        service.Delete(item.Entry);
                        meta.Remove(item.Entry.Key);
                        break;
                }

                ok++;
            }
            catch (Exception ex)
            {
                errors.Add($"{item.KindLabel} {item.Entry.ListenText} → {item.Entry.ConnectText}：{ex.Message}");
                LogService.Current?.Error($"导入执行失败：{ex.Message}");
            }
        }

        meta.Save();
        return (ok, errors.Count, errors);
    }
}
