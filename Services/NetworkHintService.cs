using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PortProxyManager.Services;

/// <summary>下拉里的一条地址建议。</summary>
public sealed record AddressHint(string Address, string Category, string? Source = null)
{
    /// <summary>下拉里显示的文本；选中后只取前面的纯 IP。</summary>
    public string Display =>
        string.IsNullOrWhiteSpace(Source)
            ? $"{Address}    · {Category}"
            : $"{Address}    · {Category}（{Source}）";
}

/// <summary>
/// 收集可作为 portproxy 目标的候选地址。
///
/// 能可靠拿到的：本机各网卡地址、默认网关。
/// WSL2 的客户机 IP 只能问 wsl.exe（宿主机看不到），因此单独做成异步、带超时。
/// Windows 沙盒和其他虚拟机的客户机没有查询接口，只能从 ARP 邻居表里发现——
/// 只要它和宿主机通信过就会留记录。
/// </summary>
public static class NetworkHintService
{
    private static readonly object Gate = new();
    private static List<AddressHint> _cache = new();
    private static DateTime _cacheAtUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    private static readonly Regex ArpRow = new(
        @"^\s*(\d{1,3}(?:\.\d{1,3}){3})\s+([0-9a-fA-F]{2}(?:-[0-9a-fA-F]{2}){5})",
        RegexOptions.Compiled);

    /// <summary>arp -a 的分组头，形如 "接口: 192.168.1.6 --- 0x5"（语言无关地取 IP）。</summary>
    private static readonly Regex ArpHeader = new(
        @"(\d{1,3}(?:\.\d{1,3}){3})[^0-9]*---",
        RegexOptions.Compiled);

    /// <summary>本机网卡 / 网关 / 虚拟网卡 / 邻居缓存，带 15 秒缓存。</summary>
    public static IReadOnlyList<AddressHint> GetHints(bool includeNeighbors = true)
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _cacheAtUtc < CacheTtl && _cache.Count > 0)
                return _cache;
        }

        var hints = Collect(includeNeighbors);

        lock (Gate)
        {
            _cache = hints;
            _cacheAtUtc = DateTime.UtcNow;
        }

        return hints;
    }

    private static List<AddressHint> Collect(bool includeNeighbors)
    {
        // 同一地址只保留优先级最高的那条
        var byAddress = new Dictionary<string, (int Priority, AddressHint Hint)>(StringComparer.OrdinalIgnoreCase);
        var broadcasts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localLabelByIp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? address, int priority, string category, string? source)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            if (!IPAddress.TryParse(address, out var ip)) return;
            if (ip.AddressFamily != AddressFamily.InterNetwork) return;
            if (IsUseless(ip)) return;

            var text = ip.ToString();
            if (byAddress.TryGetValue(text, out var existing) && existing.Priority <= priority) return;

            byAddress[text] = (priority, new AddressHint(text, category, source));
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (nic.OperationalStatus != OperationalStatus.Up) continue;

                var props = nic.GetIPProperties();
                var (category, source) = Classify(nic);

                foreach (var unicast in props.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    var ipText = unicast.Address.ToString();
                    Add(ipText, category == "本机网卡" ? 3 : 2, category, source);

                    localLabelByIp[ipText] = string.IsNullOrWhiteSpace(source) ? category : source;

                    // 记录子网广播地址，稍后把邻居表里的广播条目剔掉
                    var mask = unicast.IPv4Mask;
                    if (mask is not null)
                    {
                        var ipBytes = unicast.Address.GetAddressBytes();
                        var maskBytes = mask.GetAddressBytes();
                        if (maskBytes.Length == 4)
                        {
                            var bcast = new byte[4];
                            for (var i = 0; i < 4; i++) bcast[i] = (byte)(ipBytes[i] | (byte)~maskBytes[i]);
                            broadcasts.Add(new IPAddress(bcast).ToString());
                        }
                    }
                }

                foreach (var gateway in props.GatewayAddresses)
                {
                    if (gateway.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    Add(gateway.Address.ToString(), 0, "默认网关", source);
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"枚举网卡失败：{ex.Message}");
        }

        if (includeNeighbors) AddNeighbors(Add, broadcasts, localLabelByIp);

        return byAddress.Values
            .OrderBy(v => v.Priority)
            .ThenBy(v => v.Hint.Address, StringComparer.OrdinalIgnoreCase)
            .Select(v => v.Hint)
            .ToList();
    }

    private static void AddNeighbors(
        Action<string?, int, string, string?> add,
        HashSet<string> broadcasts,
        Dictionary<string, string> localLabelByIp)
    {
        try
        {
            var arp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "arp.exe");
            if (!File.Exists(arp)) return;

            var result = ProcRunner.Run(arp, new[] { "-a" }, timeoutMs: 4000);
            if (!result.Ok) return;

            var currentInterfaceIp = string.Empty;
            var count = 0;

            foreach (var raw in result.StdOut.Split(
                new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;

                // 先看是不是分组头（"接口: x.x.x.x --- 0x5"）
                if (line.Contains("---", StringComparison.Ordinal))
                {
                    var header = ArpHeader.Match(line);
                    if (header.Success) currentInterfaceIp = header.Groups[1].Value;
                    continue;
                }

                var match = ArpRow.Match(line.Trim());
                if (!match.Success) continue;

                var address = match.Groups[1].Value;
                if (broadcasts.Contains(address)) continue;   // 子网广播地址，不能当目标
                if (count >= 30) break;

                var label = localLabelByIp.TryGetValue(currentInterfaceIp, out var name) ? name : null;
                add(address, 4, "邻居缓存", label);
                count++;
            }
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"读取 ARP 邻居表失败：{ex.Message}");
        }
    }

    /// <summary>WSL 客户机地址。会启动默认发行版，因此单独异步调用并给短超时。</summary>
    public static async Task<IReadOnlyList<AddressHint>> GetWslHintsAsync(int timeoutMs = 4000)
    {
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (!File.Exists(wsl)) return Array.Empty<AddressHint>();

        try
        {
            var result = await Task.Run(() =>
                ProcRunner.Run(wsl, new[] { "hostname", "-I" }, timeoutMs)).ConfigureAwait(false);

            if (!result.Ok) return Array.Empty<AddressHint>();

            var hints = new List<AddressHint>();
            foreach (var token in result.StdOut.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!IPAddress.TryParse(token, out var ip)) continue;
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IsUseless(ip)) continue;

                hints.Add(new AddressHint(ip.ToString(), "WSL 客户机"));
            }

            return hints
                .GroupBy(h => h.Address)
                .Select(g => g.First())
                .ToList();
        }
        catch (Exception ex)
        {
            LogService.Current?.Info($"探测 WSL 地址跳过：{ex.Message}");
            return Array.Empty<AddressHint>();
        }
    }

    private static (string Category, string? Source) Classify(NetworkInterface nic)
    {
        var name = nic.Name;
        var desc = nic.Description ?? string.Empty;
        var both = name + " " + desc;

        if (both.Contains("WSL", StringComparison.OrdinalIgnoreCase))
            return ("虚拟网卡", "WSL");

        if (both.Contains("Sandbox", StringComparison.OrdinalIgnoreCase))
            return ("虚拟网卡", "沙盒");

        if (both.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || both.Contains("VMnet", StringComparison.OrdinalIgnoreCase))
            return ("虚拟网卡", "VMware");

        if (both.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase))
            return ("虚拟网卡", "VirtualBox");

        if (both.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
            || both.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
            return ("虚拟网卡", CleanAdapterName(name));

        if (nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel) return ("隧道", name);

        return ("本机网卡", name);
    }

    /// <summary>把 "vEthernet (Default Switch)" 整理成 "Default Switch"。</summary>
    private static string CleanAdapterName(string name)
    {
        var text = name.Replace("vEthernet ", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        return text.Trim('(', ')', ' ');
    }

    private static bool IsUseless(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();

        if (bytes[0] == 0) return true;                                  // 0.0.0.0
        if (bytes[0] == 127) return true;                                 // 回环
        if (bytes[0] == 255) return true;                                 // 广播
        if (bytes[0] >= 224) return true;                                 // 组播/保留
        if (bytes[0] == 169 && bytes[1] == 254) return true;              // 链路本地

        return false;
    }
}
