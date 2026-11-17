using System.ComponentModel;
using PortProxyManager.Models;
using PortProxyManager.Services;

namespace PortProxyManager.UI;

/// <summary>表格行。属性名即 DataGridView 的 DataPropertyName。</summary>
public sealed class EntryRow
{
    public EntryRow(PortProxyEntry entry, EntryMeta? meta)
    {
        Entry = entry;
        Meta = meta;
    }

    public PortProxyEntry Entry { get; }
    public EntryMeta? Meta { get; }

    [Browsable(false)] public string Key => Entry.Key;

    [Browsable(false)]
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
