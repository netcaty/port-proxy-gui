using PortProxyManager.Models;

namespace PortProxyManager.Services;

/// <summary>
/// 映射的增删改。写入一律经 netsh，并且每次写后重读注册表核对——
/// netsh 退出码为 0 但映射并未真正落地（或落地到了别处）是真实存在的坑。
/// </summary>
public sealed class PortProxyService
{
    public IReadOnlyList<PortProxyEntry> Snapshot(out string? error) => RegistryReader.ReadAll(out error);

    public IReadOnlyList<PortProxyEntry> Snapshot() => RegistryReader.ReadAll(out _);

    public PortProxyEntry Add(PortProxyEntry entry)
    {
        EnsureElevated();

        var result = NetshRunner.Run(
            "interface", "portproxy", "add", entry.Family, entry.Protocol,
            $"listenaddress={entry.NetshListenAddress}",
            $"listenport={entry.ListenPort}",
            $"connectaddress={entry.NetshConnectAddress}",
            $"connectport={entry.ConnectPort}");

        if (!result.Ok) throw new PortProxyException(NetshRunner.Friendly(result), result);

        return VerifyPresent(entry, result);
    }

    /// <summary>
    /// 删除一条映射。
    ///
    /// 遇到注册表里把通配监听地址存成字面量 *（非 netsh 标准写法）的遗留条目时，
    /// netsh 需要精确匹配键名，因此先试原始地址，再退回标准地址，两个都失败才报错。
    /// </summary>
    public void Delete(PortProxyEntry entry, bool useRawListenAddress = false)
    {
        EnsureElevated();

        var raw = (entry.ListenAddress ?? string.Empty).Trim();
        var candidates = new List<string>();

        if (useRawListenAddress && raw.Length > 0 && raw != entry.NetshListenAddress)
            candidates.Add(raw);

        if (!candidates.Contains(entry.NetshListenAddress))
            candidates.Add(entry.NetshListenAddress);

        if (raw.Length > 0 && !candidates.Contains(raw))
            candidates.Add(raw);

        ProcResult? lastResult = null;

        foreach (var listenAddress in candidates)
        {
            var result = NetshRunner.Run(
                "interface", "portproxy", "delete", entry.Family, entry.Protocol,
                $"listenaddress={listenAddress}",
                $"listenport={entry.ListenPort}");

            lastResult = result;

            var after = RegistryReader.ReadAll(out _);
            var remaining = after.FirstOrDefault(e => e.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase));
            if (remaining is null) return;   // 删掉了（或本来就不存在）

            if (!result.Ok) LogService.Current?.Warn($"删除用 listenaddress={listenAddress} 未成功，换下一种写法");
        }

        var stillThere = RegistryReader.ReadAll(out _)
            .FirstOrDefault(e => e.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase));

        if (stillThere is null) return;

        throw new PortProxyException(
            $"netsh 无法删除这条映射：{stillThere.Family}/{stillThere.Protocol} {stillThere.ListenText}。" +
            Environment.NewLine +
            "它很可能是直接写注册表创建的（监听地址存成了非标准形式），netsh 匹配不到。" +
            Environment.NewLine + Environment.NewLine +
            "手工清理：在 regedit 里删除" + Environment.NewLine +
            $@"HKLM\{RegistryReader.RootPath}\{stillThere.Family}\{stillThere.Protocol} 下的值 {stillThere.NormalizedListen}/{stillThere.ListenPort}",
            lastResult);
    }

    /// <summary>
    /// 修改 = 先删后加。加失败时按原值重新加上，保证不会把用户已有的映射改丢。
    /// </summary>
    public bool Update(PortProxyEntry oldEntry, PortProxyEntry newEntry)
    {
        EnsureElevated();

        if (oldEntry.SameTarget(newEntry)) return false;

        Delete(oldEntry);

        try
        {
            Add(newEntry);
        }
        catch (Exception updateEx)
        {
            LogService.Current?.Warn($"修改失败，开始回滚：{oldEntry} -> {newEntry}：{updateEx.Message}");
            try
            {
                Add(oldEntry);
            }
            catch (Exception rollbackEx)
            {
                LogService.Current?.Error($"回滚失败，旧映射可能已丢失：{rollbackEx}");
                throw new PortProxyException(
                    $"修改失败：{updateEx.Message}{Environment.NewLine}" +
                    $"回滚也失败：{rollbackEx.Message}{Environment.NewLine}" +
                    $"原映射 {oldEntry.ListenText} 现在可能已不存在，请用 backups 目录里的快照恢复。",
                    inner: rollbackEx);
            }

            throw new PortProxyException(
                $"修改失败，已把原映射恢复：{updateEx.Message}", inner: updateEx);
        }

        return true;
    }

    private PortProxyEntry VerifyPresent(PortProxyEntry expected, ProcResult result)
    {
        var entries = RegistryReader.ReadAll(out var readError);
        if (readError is not null)
        {
            throw new PortProxyException(
                $"映射已提交，但读取注册表核对时出错：{readError}", result);
        }

        var actual = entries.FirstOrDefault(e => e.Key.Equals(expected.Key, StringComparison.OrdinalIgnoreCase));
        if (actual is null)
        {
            throw new PortProxyException(
                "命令返回成功，但注册表里没有出现这条映射，改动未生效。" +
                "请确认 IP Helper（iphlpsvc）服务正在运行。", result);
        }

        if (!actual.SameTarget(expected))
        {
            throw new PortProxyException(
                $"命令返回成功，但实际落地结果与预期不一致：{Environment.NewLine}" +
                $"预期 {expected.ListenText} -> {expected.ConnectText}{Environment.NewLine}" +
                $"实际 {actual.ListenText} -> {actual.ConnectText}", result);
        }

        return actual;
    }

    private static void EnsureElevated()
    {
        if (!Elevation.IsElevated)
            throw new PortProxyException("修改端口映射需要管理员权限。请以管理员身份重新启动本程序。");
    }
}
