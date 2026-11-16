using PortProxyManager.Models;

namespace PortProxyManager.Services;

/// <summary>
/// 可选：为映射建立入站放行规则。
/// 规则名固定由监听地址+端口生成，不使用任何用户输入的自由文本。
/// </summary>
public static class FirewallService
{
    public static string RuleName(PortProxyEntry entry) =>
        $"PortProxy {entry.NormalizedListen}:{entry.ListenPort}";

    public static bool Exists(PortProxyEntry entry)
    {
        var result = NetshRunner.Run(
            "advfirewall", "firewall", "show", "rule", $"name={RuleName(entry)}");
        return result.Ok;
    }

    public static (bool Ok, string Message) Add(PortProxyEntry entry)
    {
        if (!Elevation.IsElevated)
            return (false, "创建防火墙规则需要管理员权限。");

        var result = NetshRunner.Run(
            "advfirewall", "firewall", "add", "rule",
            $"name={RuleName(entry)}",
            "dir=in",
            "action=allow",
            "protocol=TCP",
            $"localport={entry.ListenPort}");

        return result.Ok
            ? (true, $"已添加入站放行规则（TCP {entry.ListenPort}）。")
            : (false, NetshRunner.Friendly(result));
    }

    public static (bool Ok, string Message) Delete(PortProxyEntry entry)
    {
        if (!Elevation.IsElevated)
            return (false, "删除防火墙规则需要管理员权限。");

        var result = NetshRunner.Run(
            "advfirewall", "firewall", "delete", "rule", $"name={RuleName(entry)}");

        // 规则本来就不存在时也算成功（幂等）
        return result.Ok || !Exists(entry)
            ? (true, "防火墙规则已清理。")
            : (false, NetshRunner.Friendly(result));
    }
}
