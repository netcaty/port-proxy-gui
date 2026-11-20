using System.Net;
using System.Net.Sockets;
using PortProxyManager.Models;

namespace PortProxyManager.Services;

/// <summary>
/// 一条"目标跟随"规则。Kind 取 wsl / hyperv / wsb / vm；
/// Id 是发行版名、虚拟机名等，Mac 用于虚拟机改名后仍能认回同一台机器。
/// 这些字段只参与本地匹配，不会拼进 netsh 命令行。
/// </summary>
public sealed record FollowTarget(string Kind, string? Id, string? Mac, string? Label)
{
    public string KindKey => (Kind ?? string.Empty).Trim().ToLowerInvariant();

    public string Display
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Label)) return Label!;

            var id = string.IsNullOrWhiteSpace(Id) ? null : Id.Trim();
            var mac = TargetFollowService.ShortMac(Mac);
            var suffix = id is not null && !TargetFollowService.LooksLikeMacId(id) ? id : mac;

            return KindKey switch
            {
                "wsl" => suffix is null || suffix.Equals("WSL", StringComparison.OrdinalIgnoreCase)
                    ? "WSL"
                    : $"WSL · {suffix}",
                "hyperv" => $"Hyper-V · {suffix ?? "未命名"}",
                "wsb" => $"沙盒 · {suffix ?? "未命名"}",
                "vm" => $"虚拟机 · {suffix ?? "未命名"}",
                _ => $"{KindKey} · {suffix ?? "未命名"}",
            };
        }
    }

    public static FollowTarget? FromMeta(EntryMeta? meta)
    {
        if (meta?.IsFollowing != true) return null;

        return new FollowTarget(
            meta.FollowKind!.Trim().ToLowerInvariant(),
            Clean(meta.FollowId),
            TargetFollowService.NormalizeMac(meta.FollowMac),
            Clean(meta.FollowLabel));
    }

    public static FollowTarget? FromHint(AddressHint hint)
    {
        if (!TargetFollowService.IsFollowable(hint)) return null;

        return new FollowTarget(
            hint.Kind!.Trim().ToLowerInvariant(),
            Clean(hint.Id),
            TargetFollowService.NormalizeMac(hint.Mac),
            TargetFollowService.BuildLabel(hint));
    }

    internal static string? Clean(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }
}

public enum FollowSyncStatus
{
    Updated,
    UpToDate,
    Unresolved,
    Skipped,
    NeedsElevation,
    Failed,
}

public sealed record FollowSyncItem(
    PortProxyEntry Entry,
    FollowTarget Target,
    string? NewAddress,
    FollowSyncStatus Status,
    string Message)
{
    public string StatusLabel => Status switch
    {
        FollowSyncStatus.Updated => "已更新",
        FollowSyncStatus.UpToDate => "无变化",
        FollowSyncStatus.Unresolved => "未检测到",
        FollowSyncStatus.Skipped => "跳过",
        FollowSyncStatus.NeedsElevation => "待提权",
        _ => "失败",
    };
}

public sealed record FollowSyncReport(IReadOnlyList<FollowSyncItem> Items)
{
    public int UpdatedCount => Items.Count(i => i.Status == FollowSyncStatus.Updated);
    public int FailedCount => Items.Count(i => i.Status == FollowSyncStatus.Failed);
    public int NeedsElevationCount => Items.Count(i => i.Status == FollowSyncStatus.NeedsElevation);
    public int UnresolvedCount => Items.Count(i => i.Status == FollowSyncStatus.Unresolved);
}

/// <summary>
/// WSL / 虚拟机目标跟随。
///
/// portproxy 的目标 IP 是静态的，而 WSL2 NAT 和虚拟机的地址会随重启变化。
/// 这里把"跟谁"保存在 meta 里，网络变化或定时器触发时重新探测，
/// 地址变了才走 PortProxyService.Update 重建映射（仍然经过 netsh + 注册表核对）。
/// </summary>
public static class TargetFollowService
{
    /// <summary>只有 WSL / Hyper-V / 沙盒 / VMware / VirtualBox 这类客户机才值得跟随。</summary>
    public static bool IsFollowable(AddressHint hint)
    {
        if (hint.Kind is not ("wsl" or "hyperv" or "wsb" or "vm")) return false;

        // WSL 没有 MAC，用发行版名做标识；只有 MAC 的 ARP 邻居无法稳定认回发行版。
        if (hint.Kind == "wsl")
            return !string.IsNullOrWhiteSpace(hint.Id) && !LooksLikeMacId(hint.Id);

        // 虚拟机优先用 MAC，其次用名字。
        return !string.IsNullOrWhiteSpace(hint.Mac)
               || !string.IsNullOrWhiteSpace(hint.Id);
    }

    /// <summary>
    /// 判断探测结果是否还是同一条跟随规则。
    /// 虚拟机优先比 MAC（改名也不怕），MAC 缺失时退回 Id；WSL 比发行版名。
    /// </summary>
    public static bool Matches(AddressHint hint, FollowTarget target)
    {
        if (!IsFollowable(hint)) return false;

        var hintKind = (hint.Kind ?? string.Empty).Trim().ToLowerInvariant();
        var targetKind = target.KindKey;

        if (hintKind == "wsl" || targetKind == "wsl")
        {
            if (hintKind != "wsl" || targetKind != "wsl") return false;

            var targetId = FollowTarget.Clean(target.Id);
            var hintId = FollowTarget.Clean(hint.Id);

            // 只有 MAC 的 ARP 邻居不能当成某个 WSL 发行版
            if (hintId is null || LooksLikeMacId(hintId)) return false;

            // 早期探测拿不到发行版名时 Id 是通用的 "WSL"，此时接受任意 WSL 结果。
            if (targetId is null || targetId.Equals("WSL", StringComparison.OrdinalIgnoreCase))
                return true;

            return string.Equals(targetId, hintId, StringComparison.OrdinalIgnoreCase);
        }

        // 沙盒每次启动都是临时创建的 VM，MAC 从 Hyper-V 地址池重新分配、IP 跟着变，
        // 稳定身份只有 .wsb 文件名（沙盒同时只能跑一个实例）。MAC 只作展示，不参与认回。
        if (hintKind == "wsb" && targetKind == "wsb")
        {
            if (IsGenericWsbId(FollowTarget.Clean(target.Id)))
                return true; // 目标没绑定具体 .wsb 文件名（早期通用 ID），接受任意沙盒结果

            var hintId = FollowTarget.Clean(hint.Id);
            if (hintId is null || IsGenericWsbId(hintId))
                return false; // 这轮探测没拿到文件名，不能确认是同一个沙盒，宁可未检测到

            return string.Equals(FollowTarget.Clean(target.Id), hintId, StringComparison.OrdinalIgnoreCase);
        }

        var targetMac = NormalizeMac(target.Mac);
        var hintMac = NormalizeMac(hint.Mac);

        if (targetMac is not null && hintMac is not null)
        {
            // 两边都有 MAC 时以 MAC 为准；不一致就绝不能按名字误匹配。
            return string.Equals(targetMac, hintMac, StringComparison.Ordinal);
        }

        var targetName = FollowTarget.Clean(target.Id);
        var hintName = FollowTarget.Clean(hint.Id);

        // Id 本身可能就是 MAC（VMware/VirtualBox 的 ARP 兜底），格式不同也要能对上
        var targetNameMac = NormalizeMac(targetName);
        var hintNameMac = NormalizeMac(hintName);
        if (targetNameMac is not null && hintNameMac is not null)
            return string.Equals(targetNameMac, hintNameMac, StringComparison.Ordinal);

        return targetName is not null
               && hintName is not null
               && string.Equals(targetName, hintName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>没绑定具体 .wsb 文件名的通用标识（探测兜底的 "WindowsSandbox" / 空值）。</summary>
    private static bool IsGenericWsbId(string? id)
        => id is null || id.Equals("WindowsSandbox", StringComparison.OrdinalIgnoreCase);

    /// <summary>界面里判断两个跟随规则是不是同一个（用于回显已有选择）。</summary>
    public static bool TargetEquals(FollowTarget? a, FollowTarget? b)
    {
        if (a is null || b is null) return a is null && b is null;

        // 沙盒 MAC 每次启动都会变，回显也按 .wsb 文件名认，跟 Matches 语义一致
        if (a.KindKey == "wsb" && b.KindKey == "wsb")
        {
            if (IsGenericWsbId(FollowTarget.Clean(a.Id)) || IsGenericWsbId(FollowTarget.Clean(b.Id)))
                return true;
            return string.Equals(
                FollowTarget.Clean(a.Id), FollowTarget.Clean(b.Id), StringComparison.OrdinalIgnoreCase);
        }

        var aMac = NormalizeMac(a.Mac);
        var bMac = NormalizeMac(b.Mac);
        if (aMac is not null && bMac is not null)
            return string.Equals(aMac, bMac, StringComparison.Ordinal);

        if (!string.Equals(a.KindKey, b.KindKey, StringComparison.Ordinal)) return false;

        return string.Equals(
            FollowTarget.Clean(a.Id), FollowTarget.Clean(b.Id), StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildLabel(AddressHint hint)
    {
        var id = FollowTarget.Clean(hint.Id);
        var suffix = id is not null && !LooksLikeMacId(id) ? id : ShortMac(hint.Mac);

        return (hint.Kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "wsl" => suffix is null || suffix.Equals("WSL", StringComparison.OrdinalIgnoreCase)
                ? "WSL"
                : $"WSL · {suffix}",
            "hyperv" => $"Hyper-V · {suffix ?? "未命名"}",
            "wsb" => $"沙盒 · {suffix ?? "未命名"}",
            "vm" => $"虚拟机 · {suffix ?? "未命名"}",
            _ => $"{hint.Kind ?? "设备"} · {suffix ?? hint.Address}",
        };
    }

    /// <summary>下拉/表格里展示用：优先用保存下来的名字，避免 VM 离线时显示成一串 MAC。</summary>
    public static string DisplayFor(EntryMeta? meta)
    {
        var target = FollowTarget.FromMeta(meta);
        return target?.Display ?? string.Empty;
    }

    public static async Task<FollowSyncReport> SyncAsync(
        IReadOnlyList<(PortProxyEntry Entry, EntryMeta Meta)> entries,
        PortProxyService service,
        bool elevated,
        bool forceRefresh,
        IReadOnlySet<string>? onlyKeys = null,
        Action<string, string?, string?>? updateFollow = null,
        CancellationToken cancellationToken = default)
    {
        var targets = new List<(PortProxyEntry Entry, FollowTarget Target)>();

        foreach (var (entry, meta) in entries)
        {
            if (onlyKeys is not null && !onlyKeys.Contains(entry.Key)) continue;

            var target = FollowTarget.FromMeta(meta);
            if (target is null) continue;

            targets.Add((entry, target));
        }

        var items = new List<FollowSyncItem>();
        if (targets.Count == 0) return new FollowSyncReport(items);

        // 映射可能已被外部删除；跟随不能把它悄悄重建回来。
        var registry = RegistryReader.ReadAll(out var readError);
        if (readError is not null)
        {
            foreach (var target in targets)
            {
                items.Add(new FollowSyncItem(target.Entry, target.Target, null,
                    FollowSyncStatus.Failed, $"读取注册表失败：{readError}"));
            }

            return new FollowSyncReport(items);
        }

        var currentByKey = new Dictionary<string, PortProxyEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in registry) currentByKey.TryAdd(item.Key, item);

        var pending = new List<(PortProxyEntry Entry, FollowTarget Target)>();
        foreach (var target in targets)
        {
            if (!currentByKey.ContainsKey(target.Entry.Key))
            {
                items.Add(new FollowSyncItem(target.Entry, target.Target, null,
                    FollowSyncStatus.Skipped,
                    "注册表里已经没有这条映射，跳过跟随（不会自动重建）。"));
                continue;
            }

            pending.Add(target);
        }

        if (pending.Count == 0) return new FollowSyncReport(items);

        IReadOnlyList<AddressHint> hints;
        try
        {
            hints = await CollectHintsAsync(forceRefresh, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"目标跟随探测失败：{ex.Message}");
            hints = GuestHintCache.Snapshot;
        }

        foreach (var (entry, target) in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entry.ConnectIsV4)
            {
                items.Add(new FollowSyncItem(entry, target, null, FollowSyncStatus.Skipped,
                    "目标侧不是 IPv4，无法跟随 IPv4 客户机地址。"));
                continue;
            }

            var hint = hints.FirstOrDefault(h => Matches(h, target));

            if (hint is null)
            {
                items.Add(new FollowSyncItem(entry, target, null, FollowSyncStatus.Unresolved,
                    $"当前没有检测到 {target.Display}，保留原目标 {entry.ConnectText}。"));
                continue;
            }

            if (!IPAddress.TryParse(hint.Address, out var ip)
                || ip.AddressFamily != AddressFamily.InterNetwork)
            {
                items.Add(new FollowSyncItem(entry, target, null, FollowSyncStatus.Skipped,
                    $"探测到的地址「{hint.Address}」不是有效的 IPv4。"));
                continue;
            }

            var address = ip.ToString();
            RefreshFollow(updateFollow, entry.Key, target, hint);
            currentByKey.TryGetValue(entry.Key, out var current);
            var currentTarget = current?.NormalizedConnect ?? entry.NormalizedConnect;

            if (string.Equals(address, currentTarget, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new FollowSyncItem(entry, target, address, FollowSyncStatus.UpToDate,
                    $"目标未变化（{address}）。"));
                continue;
            }

            if (!elevated)
            {
                items.Add(new FollowSyncItem(entry, target, address, FollowSyncStatus.NeedsElevation,
                    $"目标已变为 {address}，但写入映射需要管理员权限。"));
                continue;
            }

            try
            {
                var previous = currentTarget;
                var oldEntry = current ?? entry;
                var updated = entry.WithConnection(address, entry.ConnectPort);

                await Task.Run(() => service.Update(oldEntry, updated), cancellationToken)
                    .ConfigureAwait(false);

                LogService.Current?.Info(
                    $"跟随 {target.Display}：{entry.ListenText} 目标 {previous} → {address}");

                items.Add(new FollowSyncItem(entry, target, address, FollowSyncStatus.Updated,
                    $"目标已更新：{previous} → {address}"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Current?.Error($"跟随 {target.Display} 更新失败：{ex.Message}");
                items.Add(new FollowSyncItem(entry, target, address, FollowSyncStatus.Failed,
                    $"更新失败：{ex.Message}"));
            }
        }

        return new FollowSyncReport(items);
    }

    /// <summary>
    /// 探测命中后把最新的 MAC / 展示文本回写 meta（值没变就不打扰调用方）。
    /// 跟随匹配不依赖 wsb 的 MAC，但 TargetEquals 与编辑界面的回显要靠它认回。
    /// </summary>
    private static void RefreshFollow(
        Action<string, string?, string?>? updateFollow, string key, FollowTarget target, AddressHint hint)
    {
        if (updateFollow is null) return;

        var mac = NormalizeMac(hint.Mac);
        var label = hint.Display;
        if (string.Equals(NormalizeMac(target.Mac), mac, StringComparison.Ordinal)
            && string.Equals(target.Label ?? string.Empty, label ?? string.Empty, StringComparison.Ordinal))
        {
            return;
        }

        LogService.Current?.Info($"跟随 {target.Display}：更新已存跟随标识（MAC {mac ?? "无"}）");
        updateFollow(key, mac, label);
    }

    private static async Task<IReadOnlyList<AddressHint>> CollectHintsAsync(
        bool forceRefresh, CancellationToken cancellationToken)
    {
        var cached = await GuestHintCache.RefreshNowAsync(forceRefresh).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // 缓存里是已确认运行中的 WSL / Hyper-V / 沙盒 / 虚拟机；
        // ARP 邻居只补充网关和局域网设备，未确认的虚拟邻居不参与跟随。
        var baseHints = NetworkHintService.FilterUnconfirmedGuests(
            await Task.Run(() => NetworkHintService.GetHints(), cancellationToken)
                .ConfigureAwait(false));

        var merged = new List<AddressHint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hint in cached.Concat(baseHints))
        {
            if (!IsFollowable(hint)) continue;
            if (seen.Add(hint.Address)) merged.Add(hint);
        }

        return merged;
    }

    internal static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;

        var chars = mac.Where(char.IsAsciiHexDigit).ToArray();
        return chars.Length == 12 ? new string(chars).ToUpperInvariant() : null;
    }

    internal static bool LooksLikeMacId(string id) => NormalizeMac(id) is not null;

    internal static string? ShortMac(string? mac)
    {
        var normalized = NormalizeMac(mac);
        return normalized is null ? null : $"MAC {normalized[^4..]}";
    }
}
