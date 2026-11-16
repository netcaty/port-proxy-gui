namespace PortProxyManager.Models;

/// <summary>
/// netsh 不支持的补充信息。按 PortProxyEntry.Key 关联，永远只用于展示，
/// 绝不拼进任何命令行（安全边界）。
/// </summary>
public sealed class EntryMeta
{
    public string? Name { get; set; }
    public string? Group { get; set; }
    public string? Note { get; set; }
    public bool HasFirewallRule { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name)
        && string.IsNullOrWhiteSpace(Group)
        && string.IsNullOrWhiteSpace(Note)
        && !HasFirewallRule;

    public EntryMeta Clone() => new()
    {
        Name = Name,
        Group = Group,
        Note = Note,
        HasFirewallRule = HasFirewallRule,
        CreatedAt = CreatedAt,
    };
}
