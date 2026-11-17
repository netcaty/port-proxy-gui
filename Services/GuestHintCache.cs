namespace PortProxyManager.Services;

/// <summary>
/// WSL / Hyper-V / Windows 沙盒 / VMware / VirtualBox / 局域网设备名的探测缓存。
///
/// 下拉打开时只读取当前快照，不启动探测；关闭下拉后才在后台刷新，
/// 下次打开生效。缓存有效期 60 秒，避免每次打开都重启子进程。
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

            var localTask = NetworkHintService.IdentifyLocalGuestsAsync(baseHints);
            var wslTask = NetworkHintService.GetWslHintsAsync();
            var namesTask = NetworkHintService.ResolveDeviceNamesAsync(baseHints);

            // 快速结果：沙盒 / Hyper-V 名称 + 局域网设备名
            var local = await SafeAsync(localTask).ConfigureAwait(false);
            var names = await SafeAsync(namesTask).ConfigureAwait(false);
            Publish(Array.Empty<AddressHint>(), local, names);

            // 慢结果：WSL 冷启动可能十几秒，完成后合并发布
            var wsl = await SafeAsync(wslTask).ConfigureAwait(false);
            if (wsl.Count > 0) Publish(wsl, local, names);
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
        IReadOnlyList<AddressHint> names)
    {
        // 优先级：WSL > 沙盒/Hyper-V > 反向解析出的设备名
        var merged = new List<AddressHint>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hint in wsl.Concat(local).Concat(names))
            if (seen.Add(hint.Address)) merged.Add(hint);

        List<AddressHint> previous;
        lock (Gate)
        {
            previous = _hints;
            _hints = merged;
            _refreshedAtUtc = DateTime.UtcNow;
        }

        var changed = !previous.Select(h => h.Display)
            .SequenceEqual(merged.Select(h => h.Display), StringComparer.Ordinal);

        if (!changed) return;

        if (merged.Count > 0)
            LogService.Current?.Info("地址候选更新：" +
                string.Join("；", merged.Select(h => h.Display)));
    }
}
