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

    /// <summary>
    /// 目标跟随。Kind 取 wsl / hyperv / wsb / vm；Id 是发行版名、虚拟机名等；
    /// Mac 用于虚拟机改名后仍能认回同一台机器。这些字段只用于本地匹配，
    /// 绝不会拼进任何命令行。
    /// </summary>
    public string? FollowKind { get; set; }
    public string? FollowId { get; set; }
    public string? FollowMac { get; set; }
    public string? FollowLabel { get; set; }

    public bool IsFollowing => !string.IsNullOrWhiteSpace(FollowKind);

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Name)
        && string.IsNullOrWhiteSpace(Group)
        && string.IsNullOrWhiteSpace(Note)
        && !HasFirewallRule
        && !IsFollowing;

    public EntryMeta Clone() => new()
    {
        Name = Name,
        Group = Group,
        Note = Note,
        HasFirewallRule = HasFirewallRule,
        CreatedAt = CreatedAt,
        FollowKind = FollowKind,
        FollowId = FollowId,
        FollowMac = FollowMac,
        FollowLabel = FollowLabel,
    };
}
