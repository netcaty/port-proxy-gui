using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PortProxyManager.Models;

namespace PortProxyManager.Services;

public enum DiagnosticStatus
{
    Pass,
    Warn,
    Fail,
    Skip,
}

public enum DiagnosticAction
{
    None,
    StartIpHelper,
    RestartIpHelper,
    RebuildMapping,
    AddFirewallRule,
}

public sealed record DiagnosticCheck(
    string Name,
    DiagnosticStatus Status,
    string Detail,
    DiagnosticAction Action = DiagnosticAction.None,
    string? ActionLabel = null)
{
    public string StatusLabel => Status switch
    {
        DiagnosticStatus.Pass => "通过",
        DiagnosticStatus.Warn => "警告",
        DiagnosticStatus.Fail => "失败",
        _ => "跳过",
    };
}

public sealed record DiagnosticReport(
    PortProxyEntry Entry,
    EntryMeta? Meta,
    DateTimeOffset At,
    IReadOnlyList<DiagnosticCheck> Checks)
{
    public int PassCount => Checks.Count(c => c.Status == DiagnosticStatus.Pass);
    public int WarnCount => Checks.Count(c => c.Status == DiagnosticStatus.Warn);
    public int FailCount => Checks.Count(c => c.Status == DiagnosticStatus.Fail);
    public int SkipCount => Checks.Count(c => c.Status == DiagnosticStatus.Skip);

    public string ToText()
    {
        var lines = new List<string>
        {
            "PortProxy 诊断报告",
            $"时间：{At:yyyy-MM-dd HH:mm:ss}",
            $"映射：{Entry.Family}/{Entry.Protocol} {Entry.ListenText} → {Entry.ConnectText}",
            $"名称：{Meta?.Name ?? "（未命名）"}",
            $"结果：通过 {PassCount}，警告 {WarnCount}，失败 {FailCount}，跳过 {SkipCount}",
            string.Empty,
        };

        foreach (var check in Checks)
            lines.Add($"[{check.StatusLabel}] {check.Name}：{check.Detail}");

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// 单条映射的转发链路诊断。按"配置 → 服务 → 监听 → 连通性 → 目标 → 防火墙"
/// 的顺序逐项检查，失败项尽量附带可直接执行的修复动作。
/// </summary>
public static class DiagnosticsService
{
    public static async Task<DiagnosticReport> RunAsync(
        PortProxyEntry entry,
        EntryMeta? meta,
        bool elevated,
        CancellationToken cancellationToken = default)
    {
        var checks = new List<DiagnosticCheck>();

        // 1. 注册表里是否真的存在这条映射
        var snapshot = RegistryReader.ReadAll(out var readError);
        var current = snapshot.FirstOrDefault(e =>
            e.Key.Equals(entry.Key, StringComparison.OrdinalIgnoreCase));

        if (readError is not null)
        {
            checks.Add(Fail("注册表映射", $"读取注册表失败：{readError}"));
        }
        else if (current is null)
        {
            checks.Add(Fail("注册表映射", "注册表里没有这条映射，配置没有真正落地或被外部删除。"));
        }
        else if (!current.SameTarget(entry))
        {
            checks.Add(new DiagnosticCheck("注册表映射", DiagnosticStatus.Warn,
                $"注册表里的目标是 {current.ConnectText}，与界面显示不一致（界面可能未刷新）。"));
        }
        else
        {
            checks.Add(Pass("注册表映射", $"条目存在，目标为 {current.ConnectText}。"));
        }

        // 2. 地址 / 类型格式
        var formatError = CheckFormat(entry);
        checks.Add(formatError is null
            ? Pass("地址与类型", $"{entry.Family}，监听 {entry.ListenText}，目标 {entry.ConnectText}。")
            : Fail("地址与类型", formatError));

        // 3. 监听地址写法（字面量 * 是历史遗留，IP Helper 往往加载不了）
        if (entry.ListenAddress.Trim() == "*")
        {
            checks.Add(new DiagnosticCheck("监听地址写法", DiagnosticStatus.Warn,
                "注册表里监听地址是字面量 *（不是 netsh 标准的 0.0.0.0），IP Helper 可能无法加载这条映射。",
                elevated ? DiagnosticAction.RebuildMapping : DiagnosticAction.None,
                elevated ? "重建为 0.0.0.0" : null));
        }
        else
        {
            checks.Add(Pass("监听地址写法", "监听地址写法正常。"));
        }

        // 4. 监听地址是否属于本机
        if (Validator.IsWildcard(entry.ListenAddress))
        {
            checks.Add(Pass("监听地址归属", "监听所有本机地址。"));
        }
        else
        {
            var found = LocalAddresses().Any(a => AddressEquals(a, entry.NetshListenAddress));
            checks.Add(found
                ? Pass("监听地址归属", $"{entry.NetshListenAddress} 是本机已启用的地址。")
                : Fail("监听地址归属",
                    $"{entry.NetshListenAddress} 不在本机任何已启用的网卡上，IP Helper 无法绑定。"));
        }

        // 5. 系统保留端口区间
        (int Start, int End)? reserved = null;
        var reservedQueryFailed = false;

        try
        {
            reserved = await Task.Run(
                    () => PortReservationService.FindContaining(entry.ListenPort), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            reservedQueryFailed = true;
            checks.Add(new DiagnosticCheck("系统保留端口", DiagnosticStatus.Warn,
                $"查询系统保留区间失败：{ex.Message}"));
        }

        if (reserved is not null)
        {
            checks.Add(new DiagnosticCheck("系统保留端口", DiagnosticStatus.Fail,
                $"监听端口 {entry.ListenPort} 落在系统保留区间 {reserved.Value.Start}–{reserved.Value.End}" +
                "（由 Hyper-V / WSL / WinNAT 占用），任何程序都无法监听它。"));
        }
        else if (!reservedQueryFailed)
        {
            checks.Add(Pass("系统保留端口", "不在系统保留区间内。"));
        }

        // 6. IP Helper 服务
        var state = IpHelperService.GetState();
        var helperRunning = state == IpHlpState.Running;

        switch (state)
        {
            case IpHlpState.Running:
                checks.Add(Pass("IP Helper 服务", "iphlpsvc 正在运行。"));
                break;

            case IpHlpState.Stopped:
                checks.Add(new DiagnosticCheck("IP Helper 服务", DiagnosticStatus.Fail,
                    "iphlpsvc 没有运行，portproxy 不会转发任何流量。",
                    elevated ? DiagnosticAction.StartIpHelper : DiagnosticAction.None,
                    elevated ? "启动 IP Helper" : null));
                break;

            case IpHlpState.NotFound:
                checks.Add(Fail("IP Helper 服务", "系统里找不到 iphlpsvc 服务，端口转发功能不可用。"));
                break;

            default:
                checks.Add(new DiagnosticCheck("IP Helper 服务", DiagnosticStatus.Warn,
                    "无法查询 iphlpsvc 状态（权限或服务管理器异常）。"));
                break;
        }

        // 7. 监听器是否已经建立
        var listenerFound = false;

        if (!helperRunning)
        {
            checks.Add(Skip("端口监听", "IP Helper 未运行，跳过监听器检查。"));
        }
        else
        {
            var listeners = RegistryReader.ActiveTcpListeners();
            var hit = listeners.FirstOrDefault(l =>
                l.Port == entry.ListenPort && ListenerAddressMatches(entry, l.Address));

            if (hit.Port != 0 && !string.IsNullOrEmpty(hit.Address))
            {
                listenerFound = true;
                checks.Add(Pass("端口监听", $"已有监听器 {hit.Address}:{hit.Port}。"));
            }
            else
            {
                checks.Add(new DiagnosticCheck("端口监听", DiagnosticStatus.Fail,
                    $"没有找到 {entry.NetshListenAddress}:{entry.ListenPort} 的监听器；" +
                    "配置存在，但 IP Helper 没有加载它。",
                    elevated ? DiagnosticAction.RestartIpHelper : DiagnosticAction.None,
                    elevated ? "重启 IP Helper" : null));
            }
        }

        // 8. 本机连接监听端口（验证监听侧本身可用）
        if (!listenerFound)
        {
            checks.Add(Skip("本机连通性", "监听器不存在，跳过本机连接测试。"));
        }
        else
        {
            var probeHost = Validator.IsWildcard(entry.ListenAddress)
                ? (entry.ListenIsV4 ? "127.0.0.1" : "::1")
                : entry.NetshListenAddress.Trim('[', ']');

            var (ok, detail) = await PortProbe.TestConnectAsync(probeHost, entry.ListenPort, 1500)
                .ConfigureAwait(false);

            checks.Add(ok
                ? Pass("本机连通性", $"连接 {probeHost}:{entry.ListenPort} 成功，监听侧可用。")
                : new DiagnosticCheck("本机连通性", DiagnosticStatus.Fail,
                    $"连接 {probeHost}:{entry.ListenPort} 失败：{detail}",
                    elevated ? DiagnosticAction.RestartIpHelper : DiagnosticAction.None,
                    elevated ? "重启 IP Helper" : null));
        }

        // 9. 目标是否可达
        if (entry.NormalizedConnect == "*")
        {
            checks.Add(Skip("目标连通性", "目标地址是所有地址，无法测试。"));
        }
        else
        {
            var targetHost = entry.NetshConnectAddress.Trim('[', ']');
            var (ok, detail) = await PortProbe.TestConnectAsync(targetHost, entry.ConnectPort, 2500)
                .ConfigureAwait(false);

            checks.Add(ok
                ? Pass("目标连通性", $"{entry.ConnectText} 可达。")
                : new DiagnosticCheck("目标连通性", DiagnosticStatus.Fail,
                    $"无法连接 {entry.ConnectText}：{detail} 映射本身仍然有效，但目标当前不可达。"));
        }

        // 10. 防火墙联动（可选）
        if (meta?.HasFirewallRule == true)
        {
            try
            {
                var exists = await Task.Run(
                        () => FirewallService.Exists(entry), cancellationToken)
                    .ConfigureAwait(false);

                checks.Add(exists
                    ? Pass("防火墙规则", $"入站放行规则「{FirewallService.RuleName(entry)}」存在。")
                    : new DiagnosticCheck("防火墙规则", DiagnosticStatus.Fail,
                        $"标记了防火墙联动，但找不到规则「{FirewallService.RuleName(entry)}」。",
                        elevated ? DiagnosticAction.AddFirewallRule : DiagnosticAction.None,
                        elevated ? "补充防火墙规则" : null));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                checks.Add(new DiagnosticCheck("防火墙规则", DiagnosticStatus.Warn,
                    $"查询防火墙规则失败：{ex.Message}"));
            }
        }
        else
        {
            checks.Add(Skip("防火墙规则",
                "未启用防火墙联动（只影响跨机访问，本机访问不受影响）。"));
        }

        return new DiagnosticReport(entry, meta, DateTimeOffset.Now, checks);
    }

    private static string? CheckFormat(PortProxyEntry entry)
    {
        if (!RegistryReader.Families.Contains(entry.Family))
            return $"未知的类型：{entry.Family}。";

        if (entry.ListenPort is < 1 or > 65535)
            return "监听端口不在 1–65535 之间。";

        if (entry.ConnectPort is < 1 or > 65535)
            return "目标端口不在 1–65535 之间。";

        if (!Validator.TryNormalizeAddress(
                entry.ListenAddress, !entry.ListenIsV4, out _, out var listenError))
            return $"监听地址：{listenError}";

        if (!Validator.TryNormalizeAddress(
                entry.ConnectAddress, !entry.ConnectIsV4, out _, out var connectError))
            return $"目标地址：{connectError}";

        if (entry.NormalizedConnect == "*")
            return "目标地址不能是所有地址。";

        return null;
    }

    private static bool ListenerAddressMatches(PortProxyEntry entry, string listenerAddress)
    {
        if (Validator.IsWildcard(entry.ListenAddress))
        {
            // portproxy 的 wildcard 监听应落在 0.0.0.0 / :: 上；
            // 只有别的程序监听在回环地址不算这条映射生效。
            return AddressEquals(listenerAddress, "0.0.0.0")
                   || AddressEquals(listenerAddress, "::");
        }

        return AddressEquals(listenerAddress, entry.NetshListenAddress);
    }

    private static HashSet<string> LocalAddresses()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "127.0.0.1",
            "::1",
        };

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                // 不按 OperationalStatus 过滤：网卡暂时断开不代表地址不属于本机，
                // 绑定是否成功由后面的监听器检查来判断。
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                    result.Add(unicast.Address.ToString());
            }
        }
        catch
        {
            // 枚举失败就只用回环地址兜底
        }

        return result;
    }

    private static bool AddressEquals(string a, string b)
    {
        if (IPAddress.TryParse(a.Trim('[', ']'), out var left)
            && IPAddress.TryParse(b.Trim('[', ']'), out var right))
        {
            if (left.IsIPv4MappedToIPv6) left = left.MapToIPv4();
            if (right.IsIPv4MappedToIPv6) right = right.MapToIPv4();
            return left.Equals(right);
        }

        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static DiagnosticCheck Pass(string name, string detail) =>
        new(name, DiagnosticStatus.Pass, detail);

    private static DiagnosticCheck Fail(string name, string detail) =>
        new(name, DiagnosticStatus.Fail, detail);

    private static DiagnosticCheck Skip(string name, string detail) =>
        new(name, DiagnosticStatus.Skip, detail);
}
