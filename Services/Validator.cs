using System.Net;
using System.Net.Sockets;
using PortProxyManager.Models;

namespace PortProxyManager.Services;

public sealed record ValidationResult(bool Ok, string? Error, string? Warning)
{
    public static ValidationResult Success(string? warning = null) => new(true, null, warning);
    public static ValidationResult Fail(string error) => new(false, error, null);
}

public static class Validator
{
    /// <summary>空串 / * / 0.0.0.0 / :: 都算作"所有地址"。</summary>
    public static bool IsWildcard(string? address) =>
        string.IsNullOrWhiteSpace(address)
        || address.Trim() is "*" or "0.0.0.0" or "::" or "[::]";

    /// <summary>
    /// 校验并归一化单个地址。ipv6 指定该侧应该是什么版本。
    /// 通配符统一返回 *。
    /// </summary>
    public static bool TryNormalizeAddress(string? input, bool ipv6, out string normalized, out string error)
    {
        normalized = "*";
        error = string.Empty;

        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0) return true;

        if (IsWildcard(text)) return true;

        if (!IPAddress.TryParse(text, out var ip))
        {
            error = $"「{text}」不是合法的 IP 地址。";
            return false;
        }

        var expected = ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        if (ip.AddressFamily != expected)
        {
            error = ipv6
                ? $"「{text}」是 IPv4 地址，这一侧要求 IPv6。"
                : $"「{text}」是 IPv6 地址，这一侧要求 IPv4。";
            return false;
        }

        normalized = text;
        return true;
    }

    /// <summary>根据两侧地址推断 family。通配符侧按 IPV4 处理（最常见）。</summary>
    public static string InferFamily(string? listenAddress, string? connectAddress)
    {
        static bool IsV6(string? a)
        {
            if (IsWildcard(a)) return false;
            return (a ?? string.Empty).Contains(':');
        }

        var listenV6 = IsV6(listenAddress);
        var connectV6 = IsV6(connectAddress);

        return (listenV6, connectV6) switch
        {
            (false, false) => "v4tov4",
            (false, true) => "v4tov6",
            (true, false) => "v6tov4",
            (true, true) => "v6tov6",
        };
    }

    /// <summary>
    /// 完整校验。editingKey 是编辑前的 Key，用于把它自己从重复检查里排除。
    /// </summary>
    public static ValidationResult Validate(
        PortProxyEntry entry,
        IEnumerable<PortProxyEntry> existing,
        string? editingKey = null)
    {
        if (entry.ListenPort is < 1 or > 65535)
            return ValidationResult.Fail("监听端口必须在 1–65535 之间。");

        if (entry.ConnectPort is < 1 or > 65535)
            return ValidationResult.Fail("目标端口必须在 1–65535 之间。");

        if (!RegistryReader.Families.Contains(entry.Family))
            return ValidationResult.Fail($"未知的类型：{entry.Family}。");

        if (!TryNormalizeAddress(entry.ListenAddress, !entry.ListenIsV4, out _, out var listenError))
            return ValidationResult.Fail($"监听地址：{listenError}");

        if (!TryNormalizeAddress(entry.ConnectAddress, !entry.ConnectIsV4, out _, out var connectError))
            return ValidationResult.Fail($"目标地址：{connectError}");

        if (entry.NormalizedConnect == "*")
            return ValidationResult.Fail(
                "目标地址不能是「所有地址」，请填写具体的 IP（例如 127.0.0.1）。");

        var duplicate = existing.FirstOrDefault(e =>
            !string.Equals(e.Key, editingKey, StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));

        if (duplicate is not null)
            return ValidationResult.Fail(
                $"已存在相同的监听：{duplicate.Family}/{duplicate.Protocol} {duplicate.ListenText}" +
                $" → {duplicate.ConnectText}。同一个监听地址和端口只能有一条映射。");

        // 落在系统保留区间的端口谁都绑不上，直接拦下比创建一条死映射好
        var reserved = PortReservationService.FindContaining(entry.ListenPort);
        if (reserved is not null)
        {
            return ValidationResult.Fail(
                $"监听端口 {entry.ListenPort} 落在系统保留区间 {reserved.Value.Start}–{reserved.Value.End}" +
                "（由 Hyper-V / WSL / WinNAT 占用）。该端口任何程序都无法监听，portproxy 也不会生效，" +
                "请换一个端口（例如加 10000）。");
        }

        var warning = CheckPortOccupancy(entry, existing);
        return ValidationResult.Success(warning);
    }

    /// <summary>
    /// 监听端口是否已被占用。已被现有 portproxy 占用的情况会在重复检查里先被拦下，
    /// 所以这里的提示主要针对"其他程序占着这个端口"。
    /// </summary>
    public static string? CheckPortOccupancy(PortProxyEntry entry, IEnumerable<PortProxyEntry> existing)
    {
        var existingList = existing.ToList();

        var ownedByPortProxy = existingList.Any(e =>
            e.ListenPort == entry.ListenPort
            && !string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));

        if (ownedByPortProxy)
            return $"端口 {entry.ListenPort} 已被另一条 portproxy 映射监听（不同地址），可能冲突。";

        var listeners = RegistryReader.ActiveTcpListeners();
        var hit = listeners.FirstOrDefault(l => l.Port == entry.ListenPort);
        if (hit.Port != 0)
            return $"端口 {entry.ListenPort} 当前已被监听（{hit.Address}），如果那是其他程序，创建后会冲突。";

        return null;
    }
}
