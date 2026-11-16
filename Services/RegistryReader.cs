using Microsoft.Win32;
using PortProxyManager.Models;

namespace PortProxyManager.Services;

/// <summary>
/// 映射的唯一真相源。注册表结构（net 的 portproxy 就是这么存的）：
///   HKLM\SYSTEM\CurrentControlSet\Services\PortProxy\&lt;family&gt;\tcp
///     值名   = 监听地址/端口
///     值数据 = 目标地址/端口
/// 这里只读；任何写入都必须走 netsh，否则运行中的 IP Helper 不会重载。
/// </summary>
public static class RegistryReader
{
    public const string RootPath = @"SYSTEM\CurrentControlSet\Services\PortProxy";

    public static readonly string[] Families = { "v4tov4", "v4tov6", "v6tov4", "v6tov6" };

    public static IReadOnlyList<PortProxyEntry> ReadAll(out string? error)
    {
        error = null;
        var list = new List<PortProxyEntry>();

        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(RootPath);
            if (root is null) return list;

            foreach (var family in Families)
            {
                using var familyKey = root.OpenSubKey(family);
                using var protoKey = familyKey?.OpenSubKey("tcp");
                if (protoKey is null) continue;

                foreach (var valueName in protoKey.GetValueNames())
                {
                    if (!TryParsePair(valueName, out var listenAddress, out var listenPort)) continue;
                    if (protoKey.GetValue(valueName) is not string data) continue;
                    if (!TryParsePair(data, out var connectAddress, out var connectPort)) continue;

                    list.Add(new PortProxyEntry
                    {
                        Family = family,
                        Protocol = "tcp",
                        ListenAddress = listenAddress,
                        ListenPort = listenPort,
                        ConnectAddress = connectAddress,
                        ConnectPort = connectPort,
                    });
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            LogService.Current?.Error($"读取注册表失败：{ex}");
        }

        return list
            .OrderBy(e => Array.IndexOf(Families, e.Family))
            .ThenBy(e => e.ListenPort)
            .ThenBy(e => e.NormalizedListen, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<PortProxyEntry> ReadAll() => ReadAll(out _);

    /// <summary>把 "地址/端口" 拆开。IPv6 里没有斜杠，所以按最后一个斜杠切。</summary>
    public static bool TryParsePair(string? text, out string address, out int port)
    {
        address = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var raw = text.Trim();
        var slash = raw.LastIndexOf('/');
        if (slash <= 0 || slash == raw.Length - 1) return false;

        address = raw[..slash].Trim();
        return int.TryParse(raw[(slash + 1)..].Trim(), out port) && port is > 0 and <= 65535;
    }

    /// <summary>当前所有监听端口（用于"是否被占用"提示）。</summary>
    public static IReadOnlyList<IPEndPointInfo> ActiveTcpListeners()
    {
        var result = new List<IPEndPointInfo>();
        try
        {
            foreach (var ep in System.Net.NetworkInformation.IPGlobalProperties
                         .GetIPGlobalProperties().GetActiveTcpListeners())
            {
                result.Add(new IPEndPointInfo(ep.Address.ToString(), ep.Port));
            }
        }
        catch
        {
            // 拿不到就当作空列表，只是少一条提示
        }

        return result;
    }

    public readonly record struct IPEndPointInfo(string Address, int Port);
}
