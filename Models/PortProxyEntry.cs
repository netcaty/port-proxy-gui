namespace PortProxyManager.Models;

/// <summary>
/// 一条 portproxy 映射。唯一标识是 family + protocol + 监听地址 + 监听端口，
/// 因为 netsh 允许同一个监听端口在不同 family 下各有一条。
/// </summary>
public sealed class PortProxyEntry
{
    public string Family { get; init; } = "v4tov4";
    public string Protocol { get; init; } = "tcp";
    public string ListenAddress { get; init; } = "*";
    public int ListenPort { get; init; }
    public string ConnectAddress { get; init; } = "";
    public int ConnectPort { get; init; }

    /// <summary>注册表里 0.0.0.0 / :: / 空串 都表示"所有地址"，统一归一化为 *。</summary>
    public static string NormalizeAddress(string? address)
    {
        var a = (address ?? string.Empty).Trim();
        if (a.Length == 0) return "*";
        if (a is "0.0.0.0" or "::" or "[::]") return "*";
        return a;
    }

    public string NormalizedListen => NormalizeAddress(ListenAddress);
    public string NormalizedConnect => NormalizeAddress(ConnectAddress);

    /// <summary>监听侧是否为 IPv4（决定通配符该如何展开成 netsh 参数）。</summary>
    public bool ListenIsV4 => Family.StartsWith("v4", StringComparison.OrdinalIgnoreCase);

    /// <summary>目标侧是否为 IPv4。family 形如 v4tov6，第 4-5 位是目标侧版本。</summary>
    public bool ConnectIsV4 =>
        Family.Length >= 6 && Family.Substring(4, 2).Equals("v4", StringComparison.OrdinalIgnoreCase);

    /// <summary>传给 netsh 的监听地址：通配符要按 family 展开，* 未必被 netsh 接受。</summary>
    public string NetshListenAddress =>
        NormalizedListen == "*" ? (ListenIsV4 ? "0.0.0.0" : "::") : NormalizedListen;

    public string NetshConnectAddress =>
        NormalizedConnect == "*" ? (ConnectIsV4 ? "0.0.0.0" : "::") : NormalizedConnect;

    public string Key => $"{Family}|{Protocol.ToLowerInvariant()}|{NormalizedListen}|{ListenPort}";

    public string ListenText => $"{NormalizedListen}:{ListenPort}";
    public string ConnectText => $"{NormalizedConnect}:{ConnectPort}";

    /// <summary>两块是否指向同一目标（用于判断"覆盖"还是"相同"）。</summary>
    public bool SameTarget(PortProxyEntry other) =>
        string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase)
        && NormalizedConnect == other.NormalizedConnect
        && ConnectPort == other.ConnectPort;

    /// <summary>界面上展示的等价 netsh 命令（不含执行）。</summary>
    public string NetshPreview =>
        $"netsh interface portproxy add {Family} {Protocol} listenaddress={NetshListenAddress} " +
        $"listenport={ListenPort} connectaddress={NetshConnectAddress} connectport={ConnectPort}";

    public PortProxyEntry WithConnection(string connectAddress, int connectPort) => new()
    {
        Family = Family,
        Protocol = Protocol,
        ListenAddress = ListenAddress,
        ListenPort = ListenPort,
        ConnectAddress = connectAddress,
        ConnectPort = connectPort,
    };

    public override string ToString() => $"{Family}/{Protocol} {ListenText} -> {ConnectText}";
}
