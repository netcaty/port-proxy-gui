namespace PortProxyManager.Services;

/// <summary>
/// WSL / Hyper-V / Windows 沙盒 / 局域网设备名的探测缓存。
///
/// 下拉打开时先拿缓存立即显示；缓存超过 60 秒才在后台刷新。
/// 沙盒 / Hyper-V / 设备名先发布一次，WSL 慢探测完成后再合并发布，
/// UI 通过 Updated 事件增量更新，不需要每次重新启动子进程。
/// </summary>
public static class GuestHintCache
{
    private static readonly object Gate = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    private static List<AddressHint> _hints = new();
    private static DateTime _refreshedAtUtc = DateTime.MinValue;
    private static bool _refreshing;

    /// <summary>后台刷新完成（可能分两次：先快速结果，再 WSL）。在后台线程触发。</summary>
    public static event Action? Updated;

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
    public static void RefreshIfStale(bool force = false)
    {
        lock (Gate)
        {
            if (_refreshing) return;

            if (!force
                && _refreshedAtUtc != DateTime.MinValue
                && DateTime.UtcNow - _refreshedAtUtc < Ttl)
                return;

            _refreshing = true;
        }

        _ = Task.Run(RefreshAsync);
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
            lock (Gate) _refreshing = false;
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

        try
        {
            Updated?.Invoke();
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"地址探测通知失败：{ex.Message}");
        }
    }
}
