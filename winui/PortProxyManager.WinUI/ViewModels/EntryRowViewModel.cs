using PortProxyManager.Models;
using PortProxyManager.Services;

namespace PortProxyManager.WinUI.ViewModels;

/// <summary>
/// 表格行。旧版 UI\EntryRow.cs 的投影逻辑，DataGrid 列按属性名绑定。
/// 行不可变；数据变化时整体重建集合。
/// </summary>
public sealed class EntryRowViewModel
{
    public EntryRowViewModel(PortProxyEntry entry, EntryMeta? meta)
    {
        Entry = entry;
        Meta = meta;
    }

    public PortProxyEntry Entry { get; }
    public EntryMeta? Meta { get; }

    public string Key => Entry.Key;

    /// <summary>过滤用的全文匹配串。</summary>
    public string FullText =>
        $"{Name} {Group} {Entry.Family} {Entry.ListenText} {Entry.ConnectText} {Note} {Follow}";

    public string Name => Meta?.Name ?? string.Empty;
    public string Group => Meta?.Group ?? string.Empty;
    public string Type => Entry.Family;
    public string ListenAddress => Entry.NormalizedListen;
    public int ListenPort => Entry.ListenPort;
    public string ConnectAddress => Entry.NormalizedConnect;
    public int ConnectPort => Entry.ConnectPort;
    public string Follow => TargetFollowService.DisplayFor(Meta);
    public string Firewall => Meta?.HasFirewallRule == true ? "已放行" : string.Empty;
    public string Note => Meta?.Note ?? string.Empty;
}
