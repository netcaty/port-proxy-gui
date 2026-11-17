namespace PortProxyManager.Services;

/// <summary>
/// 目标地址候选的统一缓存：默认网关、ARP 邻居、WSL / Hyper-V / Windows 沙盒 /
/// VMware / VirtualBox，以及反向解析出的局域网设备名。
///
/// 程序启动时会在后台预热一次；下拉打开时只读取当前快照，不启动探测；
/// 关闭下拉后才在后台刷新，下次打开生效。缓存有效期 60 秒，避免每次打开都重启子进程。
/// </summary>
public static class GuestHintCache
{
    private static readonly object Gate = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private static List<AddressHint> _hints = new();
    private static DateTime _refreshedAtUtc = DateTime.MinValue;
    private static Task? _refreshTask;

    public static IReadOnlyList<AddressHint> Snapshot
    {
        get
        {
            lock (Gate) return _hints;
        }
    }

    /// <summary>
    /// 缓存过期时启动一次后台刷新。正在刷新或缓存仍新鲜时直接返回。
    /// </summary>
    public static void RefreshIfStale(bool force = false) => EnsureRefresh(force);

    /// <summary>
    /// 等待一次刷新完成，供目标跟随在写映射前拿到最新地址。
    /// force=true 时保证结果来自本次调用开始之后的探测：如果只能加入一个
    /// 已经在跑的任务，会在它结束后再补一次刷新。
    /// </summary>
    public static async Task<IReadOnlyList<AddressHint>> RefreshNowAsync(bool force = false)
    {
        var started = EnsureRefresh(force);
        await AwaitCurrentAsync().ConfigureAwait(false);

        if (force && !started)
        {
            EnsureRefresh(force: true);
            await AwaitCurrentAsync().ConfigureAwait(false);
        }

        return Snapshot;
    }

    /// <summary>返回是否真的启动了一次新刷新。</summary>
    private static bool EnsureRefresh(bool force)
    {
        lock (Gate)
        {
            if (_refreshTask is not null) return false;

            if (!force
                && _refreshedAtUtc != DateTime.MinValue
                && DateTime.UtcNow - _refreshedAtUtc < Ttl)
                return false;

            _refreshTask = Task.Run(RefreshAsync);
            return true;
        }
    }

    private static async Task AwaitCurrentAsync()
    {
        Task? task;
        lock (Gate) task = _refreshTask;

        if (task is null) return;

        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // RefreshAsync 自己会记录异常，这里只负责等待
        }
    }

    private static async Task RefreshAsync()
    {
        try
        {
            var baseHints = NetworkHintService.GetHints();

            // 只有缓存为空（通常是刚启动）时才先发布网关/ARP 邻居，
            // 避免 WSL / 虚拟机探测较慢时下拉长时间为空。
            // 已有完整快照时保留旧列表，等本次 local/names 结果出来再整体替换，
            // 否则每次刷新都会短暂退化成"只有网关 + ARP"。
            if (Snapshot.Count == 0)
            {
                Publish(
                    Array.Empty<AddressHint>(), Array.Empty<AddressHint>(),
                    Array.Empty<AddressHint>(), baseHints);
            }

            var localTask = NetworkHintService.IdentifyLocalGuestsAsync(baseHints);
            var wslTask = NetworkHintService.GetWslHintsAsync();
            var namesTask = NetworkHintService.ResolveDeviceNamesAsync(baseHints);

            // 快速结果：沙盒 / Hyper-V 名称 + 局域网设备名
            var local = await SafeAsync(localTask).ConfigureAwait(false);
            var names = await SafeAsync(namesTask).ConfigureAwait(false);
            Publish(Array.Empty<AddressHint>(), local, names, baseHints);

            // 慢结果：WSL 冷启动可能十几秒，完成后合并发布
            var wsl = await SafeAsync(wslTask).ConfigureAwait(false);
            if (wsl.Count > 0) Publish(wsl, local, names, baseHints);
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"刷新地址探测缓存失败：{ex.Message}");
        }
        finally
        {
            lock (Gate)
            {
                _refreshTask = null;
            }
        }
    }

    private static async Task<IReadOnlyList<AddressHint>> SafeAsync(
        Task<IReadOnlyList<AddressHint>> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Current?.Info($"地址探测子任务跳过：{ex.Message}");
            return Array.Empty<AddressHint>();
        }
    }

    private static void Publish(
        IReadOnlyList<AddressHint> wsl,
        IReadOnlyList<AddressHint> local,
        IReadOnlyList<AddressHint> names,
        IReadOnlyList<AddressHint> baseHints)
    {
        // 网关/局域网设备可以直接用 ARP；虚拟机/沙盒必须由探测确认运行中
        baseHints = NetworkHintService.FilterUnconfirmedGuests(baseHints);

        // 优先级：WSL > 已识别的沙盒/Hyper-V > 反向解析出的设备名 > 原始网关/ARP 邻居
        var merged = new List<AddressHint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hint in wsl.Concat(local).Concat(names).Concat(baseHints))
            if (seen.Add(hint.Address)) merged.Add(hint);

        List<AddressHint> previous;
        lock (Gate)
        {
            previous = _hints;
            _hints = merged;
            _refreshedAtUtc = DateTime.UtcNow;
        }

        // 顺序变化不算内容变化，避免 ARP 顺序抖动导致每次都刷日志
        var changed = !previous.Select(h => h.Display)
            .OrderBy(s => s, StringComparer.Ordinal)
            .SequenceEqual(
                merged.Select(h => h.Display).OrderBy(s => s, StringComparer.Ordinal),
                StringComparer.Ordinal);

        if (!changed) return;

        if (merged.Count > 0)
        {
            var sample = string.Join("；", merged.Take(12).Select(h => h.Display));
            var suffix = merged.Count > 12 ? $" …共 {merged.Count} 条" : string.Empty;
            LogService.Current?.Info("地址候选更新：" + sample + suffix);
        }
    }
}
