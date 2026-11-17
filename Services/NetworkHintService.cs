using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PortProxyManager.Services;

/// <summary>下拉里的一条地址建议。</summary>
public sealed record AddressHint(
    string Address,
    string Category,
    string? Source = null,
    string? Detail = null,
    string? Mac = null)
{
    /// <summary>下拉里显示的文本；选中后只取前面的纯 IP。</summary>
    public string Display
    {
        get
        {
            var text = $"{Address}  ·  {Category}";
            if (!string.IsNullOrWhiteSpace(Source)) text += $"（{Source}）";
            if (!string.IsNullOrWhiteSpace(Detail)) text += $"  —  {Detail}";
            return text;
        }
    }
}

/// <summary>
/// 收集可作为 portproxy 目标的候选地址，并尽量说明"这个 IP 是谁"。
///
/// 下拉只列默认网关、虚拟机和邻居设备；本机网卡 / 虚拟网卡地址不展示。
/// MAC 只在内部用于区分 Hyper-V / VMware / VirtualBox 和识别 Windows 沙盒，
/// 不显示给用户。局域网设备名通过系统反向解析（DNS / mDNS）补充。
/// 有管理员权限时再问 Hyper-V（Get-VMNetworkAdapter）换出虚拟机名称，
/// WSL 的客户机 IP 只能问 wsl.exe（宿主机看不到）。
/// Windows 沙盒没有公开的查询接口，只能结合 Default Switch、微软 OUI
/// 和 WindowsSandbox 进程窗口做启发式判断，并从进程命令行取出 .wsb 文件名，
/// 因此标注为"疑似"。
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

    private enum AdapterKind
    {
        Physical,
        Wsl,
        HyperV,
        DefaultSwitch,
        Sandbox,
        VMware,
        VirtualBox,
        Tunnel,
    }

    private sealed record AdapterInfo(AdapterKind Kind, string Label);

    private sealed record LocalAdapter(
        string Address,
        int PrefixLength,
        AdapterKind Kind,
        string Label);

    private sealed record LocalNetwork(
        List<LocalAdapter> Adapters,
        List<(string Address, string Label)> Gateways,
        HashSet<string> Broadcasts);

    /// <summary>
    /// 下拉候选：默认网关 + ARP 邻居（本机网卡和虚拟网卡地址不再列出）。
    /// 带 15 秒缓存。
    /// </summary>
    public static IReadOnlyList<AddressHint> GetHints(bool includeNeighbors = true)
    {
        // 不带邻居的版本不缓存，避免把邻居列表覆盖掉
        if (includeNeighbors)
        {
            lock (Gate)
            {
                if (DateTime.UtcNow - _cacheAtUtc < CacheTtl && _cache.Count > 0)
                    return _cache;
            }
        }

        var hints = Collect(includeNeighbors);

        if (includeNeighbors)
        {
            lock (Gate)
            {
                _cache = hints;
                _cacheAtUtc = DateTime.UtcNow;
            }
        }

        return hints;
    }

    private static List<AddressHint> Collect(bool includeNeighbors)
    {
        // 同一地址只保留优先级最高的那条
        var byAddress = new Dictionary<string, (int Priority, AddressHint Hint)>(StringComparer.OrdinalIgnoreCase);

        void Add(
            string? address, int priority, string category, string? source,
            string? detail = null, string? mac = null)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            if (!IPAddress.TryParse(address, out var ip)) return;
            if (ip.AddressFamily != AddressFamily.InterNetwork) return;
            if (IsUseless(ip)) return;

            var text = ip.ToString();
            if (byAddress.TryGetValue(text, out var existing) && existing.Priority <= priority) return;

            byAddress[text] = (priority, new AddressHint(text, category, source, detail, mac));
        }

        LocalNetwork network;
        try
        {
            network = EnumerateLocalNetwork();
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"枚举网卡失败：{ex.Message}");
            network = new LocalNetwork(new(), new(), new(StringComparer.OrdinalIgnoreCase));
        }

        foreach (var gateway in network.Gateways)
            Add(gateway.Address, 0, "默认网关", gateway.Label);

        if (includeNeighbors) AddNeighbors(Add, network);

        return byAddress.Values
            .OrderBy(v => v.Priority)
            .ThenBy(v => v.Hint.Address, StringComparer.OrdinalIgnoreCase)
            .Select(v => v.Hint)
            .ToList();
    }

    private static LocalNetwork EnumerateLocalNetwork()
    {
        var adapters = new List<LocalAdapter>();
        var gateways = new List<(string Address, string Label)>();
        var broadcasts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            var props = nic.GetIPProperties();
            var info = Classify(nic);

            foreach (var unicast in props.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IsUseless(unicast.Address)) continue;

                adapters.Add(new LocalAdapter(
                    unicast.Address.ToString(),
                    unicast.PrefixLength,
                    info.Kind,
                    info.Label));

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
                if (IsUseless(gateway.Address)) continue;
                gateways.Add((gateway.Address.ToString(), info.Label));
            }
        }

        return new LocalNetwork(adapters, gateways, broadcasts);
    }

    private static void AddNeighbors(
        Action<string?, int, string, string?, string?, string?> add,
        LocalNetwork network)
    {
        try
        {
            var arp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "arp.exe");
            if (!File.Exists(arp)) return;

            var result = ProcRunner.Run(arp, new[] { "-a" }, timeoutMs: 4000);
            if (!result.Ok) return;

            var byInterface = network.Adapters
                .GroupBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

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

                var match = ArpRow.Match(line);
                if (!match.Success) continue;

                var address = match.Groups[1].Value;
                var mac = NormalizeMac(match.Groups[2].Value);
                if (mac is null) continue;
                if (IsBroadcastOrMulticast(address, mac, network)) continue;
                if (count >= 30) break;

                byInterface.TryGetValue(currentInterfaceIp, out var adapter);
                var (category, source) = DescribeNeighbor(adapter);
                add(address, 4, category, source, null, mac);
                count++;
            }
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"读取 ARP 邻居表失败：{ex.Message}");
        }
    }

    private static bool IsBroadcastOrMulticast(string address, string mac, LocalNetwork network)
    {
        if (network.Broadcasts.Contains(address)) return true;
        if (mac is "FFFFFFFFFFFF" or "000000000000") return true;
        // 01-00-5E 是 IPv4 组播映射的 MAC
        if (mac.StartsWith("01005E", StringComparison.Ordinal)) return true;
        return IPAddress.TryParse(address, out var ip) && IsUseless(ip);
    }

    /// <summary>根据邻居所在的接口，说明它大概来自哪张网卡 / 哪个虚拟交换机。</summary>
    private static (string Category, string? Source) DescribeNeighbor(LocalAdapter? adapter)
    {
        if (adapter is null) return ("局域网设备", null);

        return adapter.Kind switch
        {
            AdapterKind.Wsl => ("WSL 虚拟机", adapter.Label),
            AdapterKind.DefaultSwitch => ("Hyper-V/沙盒虚拟机", adapter.Label),
            AdapterKind.HyperV => ("Hyper-V 虚拟机", adapter.Label),
            AdapterKind.Sandbox => ("Windows 沙盒", adapter.Label),
            AdapterKind.VMware => ("VMware 虚拟机", adapter.Label),
            AdapterKind.VirtualBox => ("VirtualBox 虚拟机", adapter.Label),
            AdapterKind.Tunnel => ("隧道对端", adapter.Label),
            _ => ("局域网设备", adapter.Label),
        };
    }

    private static readonly object NameGate = new();
    private static readonly Dictionary<string, (string? Name, DateTime AtUtc)> NameCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan NameHitTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NameMissTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 反向解析局域网设备名（Windows 解析器会同时尝试 DNS / mDNS 等）。
    /// 解析不到就保持无名称；结果带缓存，避免每次刷新都发查询。
    /// </summary>
    public static async Task<IReadOnlyList<AddressHint>> ResolveDeviceNamesAsync(
        IReadOnlyList<AddressHint> baseHints,
        int perLookupTimeoutMs = 800)
    {
        var targets = baseHints
            .Where(IsNameCandidate)
            .GroupBy(h => h.Address, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(12)
            .ToList();

        if (targets.Count == 0) return Array.Empty<AddressHint>();

        var tasks = targets.Select(h => ResolveOneNameAsync(h, perLookupTimeoutMs)).ToList();

        // 单个解析有取消超时，这里再兜一层总超时，避免某个系统解析卡住整体刷新
        await Task.WhenAny(
            Task.WhenAll(tasks),
            Task.Delay(perLookupTimeoutMs + 700)).ConfigureAwait(false);

        return tasks
            .Where(t => t.IsCompletedSuccessfully)
            .Select(t => t.Result)
            .Where(r => r.Name is not null)
            .Select(r => r.Hint with { Detail = r.Name })
            .ToList();
    }

    private static bool IsNameCandidate(AddressHint hint)
    {
        // 只给局域网设备和网关解析名称；虚拟机/沙盒已有更准确的名称来源，
        // 反向解析出来的 GUID.mshome.net 之类反而没有意义。
        return hint.Category is "局域网设备" or "默认网关";
    }

    private static async Task<(AddressHint Hint, string? Name)> ResolveOneNameAsync(
        AddressHint hint,
        int timeoutMs)
    {
        if (TryGetCachedName(hint.Address, out var cached))
            return (hint, cached);

        var name = await LookupHostNameAsync(hint.Address, timeoutMs).ConfigureAwait(false);
        StoreName(hint.Address, name);
        return (hint, name);
    }

    private static async Task<string?> LookupHostNameAsync(string address, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var entry = await Dns.GetHostEntryAsync(address, cts.Token).ConfigureAwait(false);
            return SanitizeHostName(entry.HostName, address);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetCachedName(string address, out string? name)
    {
        lock (NameGate)
        {
            if (NameCache.TryGetValue(address, out var entry))
            {
                var ttl = entry.Name is null ? NameMissTtl : NameHitTtl;
                if (DateTime.UtcNow - entry.AtUtc < ttl)
                {
                    name = entry.Name;
                    return true;
                }
            }
        }

        name = null;
        return false;
    }

    private static void StoreName(string address, string? name)
    {
        lock (NameGate)
            NameCache[address] = (name, DateTime.UtcNow);
    }

    private static string? SanitizeHostName(string? name, string address)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var text = name.Trim().TrimEnd('.');
        if (text.Length == 0) return null;
        if (IPAddress.TryParse(text, out _)) return null;
        if (text.Equals(address, StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return null;
        if (text.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return null;

        return text;
    }

    /// <summary>
    /// 只识别本机虚拟化来源（Hyper-V 名称、Windows 沙盒），不启动 wsl.exe。
    /// UI 先等这一份快速结果，再让 WSL 结果后补，避免沙盒信息被 WSL 冷启动拖住。
    /// </summary>
    public static async Task<IReadOnlyList<AddressHint>> IdentifyLocalGuestsAsync(
        IReadOnlyList<AddressHint>? baseHints = null,
        int timeoutMs = 6000)
    {
        baseHints ??= GetHints();

        var vmTask = ProbeHyperVAsync(baseHints, Math.Min(timeoutMs, 8000));
        var sandboxRunning = IsWindowsSandboxRunning();
        var sandboxNameTask = sandboxRunning
            ? GetWindowsSandboxNameAsync(Math.Min(timeoutMs, 4000))
            : Task.FromResult<string?>(null);

        try
        {
            await Task.WhenAll(vmTask, sandboxNameTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"识别本机虚拟机失败：{ex.Message}");
        }

        var identified = new List<AddressHint>();

        if (vmTask.IsCompletedSuccessfully) identified.AddRange(vmTask.Result);

        // 沙盒没有查询接口：正在运行且该地址未被已知的 Hyper-V 虚拟机认领，
        // 才把 Default Switch 上的微软 OUI 设备标成"疑似"。
        if (sandboxRunning)
        {
            var sandboxName = sandboxNameTask.IsCompletedSuccessfully ? sandboxNameTask.Result : null;
            AddSandboxHints(baseHints, vmTask, sandboxName, identified);
        }

        return Deduplicate(identified);
    }

    private static List<AddressHint> Deduplicate(IEnumerable<AddressHint> hints)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return hints.Where(h => seen.Add(h.Address)).ToList();
    }

    private static void AddSandboxHints(
        IReadOnlyList<AddressHint> baseHints,
        Task<IReadOnlyList<AddressHint>> vmTask,
        string? sandboxName,
        List<AddressHint> identified)
    {
        var vmHints = vmTask.IsCompletedSuccessfully
            ? vmTask.Result
            : (IReadOnlyList<AddressHint>)Array.Empty<AddressHint>();

        var claimedIps = new HashSet<string>(vmHints.Select(h => h.Address), StringComparer.OrdinalIgnoreCase);
        var claimedMacs = new HashSet<string>(
            vmHints.Select(h => NormalizeMac(h.Mac)).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        // 以 Default Switch 上未被 Hyper-V 虚拟机认领的微软 OUI 邻居为锚点
        var anchors = baseHints
            .Where(h => h.Mac is not null
                && !claimedIps.Contains(h.Address)
                && !claimedMacs.Contains(h.Mac)
                && LooksLikeDefaultSwitch(h)
                && MacVendor(h.Mac) == "微软虚拟网卡")
            .GroupBy(h => h.Mac!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        foreach (var anchor in anchors)
        {
            var mac = anchor.Mac!;

            // 沙盒可能有多块虚拟网卡（Default Switch / MyApps vSwitch 等），
            // 同一 MAC 合并成一条，优先保留 Default Switch 地址。
            var group = baseHints
                .Where(h => h.Mac is not null
                    && string.Equals(h.Mac, mac, StringComparison.OrdinalIgnoreCase)
                    && !claimedIps.Contains(h.Address))
                .OrderBy(h => LooksLikeDefaultSwitch(h) ? 0 : 1)
                .ThenBy(h => h.Address, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var primary = group.FirstOrDefault(LooksLikeDefaultSwitch) ?? group[0];

            var alternates = group
                .Where(h => !string.Equals(h.Address, primary.Address, StringComparison.OrdinalIgnoreCase))
                .GroupBy(h => h.Address, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .Take(2)
                .ToList();

            var detailParts = new List<string>();
            if (sandboxName is not null && primary.Source is not null)
                detailParts.Add(primary.Source);
            detailParts.Add("WSB 正在运行");

            if (alternates.Count > 0)
            {
                var altText = string.Join("、", alternates.Select(a =>
                    a.Source is null ? a.Address : $"{a.Address}（{a.Source}）"));
                detailParts.Add($"另有 {altText}");
            }

            identified.Add(primary with
            {
                Category = "疑似 Windows 沙盒",
                Source = sandboxName ?? primary.Source,
                Detail = string.Join(" · ", detailParts),
            });
        }
    }

    /// <summary>
    /// 从 WindowsSandbox 进程命令行里取 .wsb 文件名，例如"微信沙盒.wsb"。
    /// 新版沙盒把 .wsb 路径放在 WindowsSandboxRemoteSession.exe 的参数里。
    /// </summary>
    private static async Task<string?> GetWindowsSandboxNameAsync(int timeoutMs)
    {
        try
        {
            var ps = PowerShellExe;
            if (!File.Exists(ps)) return null;

            const string script = """
                [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false);
                $ErrorActionPreference = 'SilentlyContinue';
                Get-CimInstance Win32_Process -Filter "Name LIKE 'WindowsSandbox%'" |
                  Select-Object Name,CommandLine | ConvertTo-Json -Compress
                """;

            var result = await Task.Run(() => ProcRunner.Run(
                ps,
                new[]
                {
                    "-NoProfile", "-NonInteractive", "-NoLogo",
                    "-ExecutionPolicy", "Bypass", "-Command", script,
                },
                timeoutMs,
                ProcTextEncoding.Utf8)).ConfigureAwait(false);

            if (!result.Ok) return null;

            var json = result.StdOut.Trim();
            var start = json.IndexOf('[');
            if (start < 0) start = json.IndexOf('{');
            if (start < 0) return null;

            using var doc = JsonDocument.Parse(json[start..]);
            var names = new List<string>();

            foreach (var process in AsArray(doc.RootElement))
            {
                var name = ExtractWsbName(JsonString(process, "CommandLine"));
                if (name is not null) names.Add(name);
            }

            var distinct = names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count == 0) return null;

            var text = string.Join("、", distinct.Take(2));
            return distinct.Count > 2 ? text + " 等" : text;
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (Exception ex)
        {
            LogService.Current?.Info($"读取 Windows 沙盒名称跳过：{ex.Message}");
            return null;
        }
    }

    private static string? ExtractWsbName(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        var quoted = Regex.Match(commandLine, "\"([^\"]+\\.wsb)\"", RegexOptions.IgnoreCase);
        var path = quoted.Success ? quoted.Groups[1].Value : null;

        if (path is null)
        {
            var bare = Regex.Match(commandLine, @"([^\s""]+\.wsb)", RegexOptions.IgnoreCase);
            if (bare.Success) path = bare.Groups[1].Value;
        }

        if (path is null) return null;

        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksLikeDefaultSwitch(AddressHint hint)
    {
        if (hint.Source?.Contains("Default Switch", StringComparison.OrdinalIgnoreCase) == true) return true;
        return hint.Category.Contains("沙盒", StringComparison.Ordinal);
    }

    /// <summary>
    /// WSL 客户机地址。只查询已在运行的发行版，不会为了探测而启动 WSL。
    /// </summary>
    public static async Task<IReadOnlyList<AddressHint>> GetWslHintsAsync(int timeoutMs = 12000)
    {
        var wsl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");
        if (!File.Exists(wsl)) return Array.Empty<AddressHint>();

        try
        {
            var result = await Task.Run(() =>
            {
                // 先查正在运行的发行版；-l --running 不会启动任何发行版。
                // wsl -l 的列表是 UTF-16LE。
                var listResult = ProcRunner.Run(
                    wsl, new[] { "-l", "--running", "-q" }, Math.Min(timeoutMs, 3000),
                    ProcTextEncoding.Utf16);
                var distros = ParseWslDistros(listResult);
                if (distros.Count == 0)
                    return (Ip: (ProcResult?)null, Distros: distros);

                // 指定已运行的发行版执行 hostname -I，避免把默认发行版拉起来。
                // hostname -I 的 stdout 是 Linux 命令原始输出（ASCII/UTF-8）。
                ProcResult? ipResult = null;
                foreach (var distro in distros)
                {
                    var probe = ProcRunner.Run(
                        wsl, new[] { "-d", distro, "hostname", "-I" }, timeoutMs);
                    if (probe.Ok)
                    {
                        ipResult = probe;
                        break;
                    }
                }

                return (Ip: ipResult, Distros: distros);
            }).ConfigureAwait(false);

            if (result.Ip is null || !result.Ip.Ok) return Array.Empty<AddressHint>();

            var localNetwork = EnumerateLocalNetwork();
            var localIps = new HashSet<string>(
                localNetwork.Adapters.Select(a => a.Address),
                StringComparer.OrdinalIgnoreCase);

            var reported = ParseIpv4List(result.Ip.StdOut);
            var ips = reported
                .Where(ip => !localIps.Contains(ip))
                .ToList();

            var distroLabel = FormatDistroNames(result.Distros);

            // 镜像网络（networkingMode=mirrored）下 WSL 与宿主机共用网络命名空间，
            // hostname -I 返回的是宿主机地址，不能当成 WSL 虚拟机的独立 IP。
            var mirrored = reported.Any(localIps.Contains);

            if (mirrored)
            {
                var detail = distroLabel is null
                    ? "与 Windows 共用 localhost，直接用 127.0.0.1"
                    : $"{distroLabel} 与 Windows 共用 localhost";
                return new[]
                {
                    new AddressHint("127.0.0.1", "WSL 客户机", "镜像网络", detail),
                };
            }

            if (ips.Count == 0) return Array.Empty<AddressHint>();

            // NAT 模式下只保留落在 WSL 虚拟网卡子网里的地址；
            // docker0 等发行版内部网桥（172.17.x.x）从宿主机不可达，要剔掉。
            var wslNets = localNetwork.Adapters
                .Where(a => a.Kind == AdapterKind.Wsl)
                .ToList();

            var candidates = wslNets.Count == 0
                ? ips
                : ips.Where(ip => wslNets.Any(n => InSubnet(ip, n.Address, n.PrefixLength))).ToList();

            if (candidates.Count == 0) candidates = ips;

            var source = distroLabel ?? "WSL";
            return candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(ip => new AddressHint(ip, "WSL 客户机", source, "NAT 虚拟网络"))
                .ToList();
        }
        catch (Exception ex)
        {
            LogService.Current?.Info($"探测 WSL 地址跳过：{ex.Message}");
            return Array.Empty<AddressHint>();
        }
    }

    private static List<string> ParseIpv4List(string text)
    {
        var result = new List<string>();
        foreach (var token in text.Split(
            new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IPAddress.TryParse(token, out var ip)) continue;
            if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
            if (IsUseless(ip)) continue;
            result.Add(ip.ToString());
        }
        return result;
    }

    private static List<string> ParseWslDistros(ProcResult result)
    {
        if (!result.Ok) return new List<string>();

        return result.StdOut
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Trim('\uFEFF', '\0'))
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FormatDistroNames(List<string> names)
    {
        if (names.Count == 0) return null;
        var text = string.Join("、", names.Take(3));
        return names.Count > 3 ? text + " 等" : text;
    }

    private static string PowerShellExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>
    /// 有 Hyper-V 模块且当前进程有权限时，用 Get-VMNetworkAdapter 把
    /// MAC / KVP 上报的 IP 映射到虚拟机名称。没权限时静默跳过。
    /// </summary>
    private static async Task<IReadOnlyList<AddressHint>> ProbeHyperVAsync(
        IReadOnlyList<AddressHint> baseHints,
        int timeoutMs)
    {
        try
        {
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var module = Path.Combine(system, "WindowsPowerShell", "v1.0", "Modules", "Hyper-V");
            var ps = PowerShellExe;
            if (!Directory.Exists(module) || !File.Exists(ps)) return Array.Empty<AddressHint>();

            const string script = """
                [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false);
                $ErrorActionPreference = 'SilentlyContinue';
                $nics = @(Get-VMNetworkAdapter -All | Where-Object { $_.MacAddress -and -not $_.IsManagementOs } | Select-Object VMName,SwitchName,MacAddress,IPAddresses);
                $vms = @(Get-VM | Select-Object Name,@{n='State';e={[string]$_.State}});
                [pscustomobject]@{ nics = $nics; vms = $vms } | ConvertTo-Json -Depth 5 -Compress
                """;

            var result = await Task.Run(() => ProcRunner.Run(
                ps,
                new[]
                {
                    "-NoProfile", "-NonInteractive", "-NoLogo",
                    "-ExecutionPolicy", "Bypass", "-Command", script,
                },
                timeoutMs,
                ProcTextEncoding.Utf8)).ConfigureAwait(false);

            if (!result.Ok) return Array.Empty<AddressHint>();

            var json = result.StdOut.Trim();
            var start = json.IndexOf('{');
            if (start < 0) return Array.Empty<AddressHint>();

            using var doc = JsonDocument.Parse(json[start..]);
            var root = doc.RootElement;

            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("vms", out var vmsElement))
            {
                foreach (var vm in AsArray(vmsElement))
                {
                    var name = JsonString(vm, "Name");
                    var state = JsonString(vm, "State");
                    if (!string.IsNullOrWhiteSpace(name))
                        states[name.Trim()] = state ?? string.Empty;
                }
            }

            if (!root.TryGetProperty("nics", out var nicsElement)) return Array.Empty<AddressHint>();

            var localIps = new HashSet<string>(
                EnumerateLocalNetwork().Adapters.Select(a => a.Address),
                StringComparer.OrdinalIgnoreCase);

            var hints = new List<AddressHint>();
            var usedIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var nic in AsArray(nicsElement))
            {
                var vmName = JsonString(nic, "VMName")?.Trim();
                if (string.IsNullOrWhiteSpace(vmName)) continue;
                if (vmName.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) continue;

                states.TryGetValue(vmName, out var state);
                // 已关闭的虚拟机在 ARP 里的地址可能已经分给别的设备，不能拿来当目标
                if (!string.IsNullOrEmpty(state)
                    && !state.Equals("Running", StringComparison.OrdinalIgnoreCase))
                    continue;

                var mac = NormalizeMac(JsonString(nic, "MacAddress"));

                var ips = new List<string>();
                if (nic.TryGetProperty("IPAddresses", out var addresses))
                {
                    foreach (var item in AsArray(addresses))
                    {
                        if (item.ValueKind != JsonValueKind.String) continue;
                        var text = item.GetString();
                        if (!IPAddress.TryParse(text, out var ip)) continue;
                        if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                        if (IsUseless(ip)) continue;
                        if (localIps.Contains(ip.ToString())) continue;
                        ips.Add(ip.ToString());
                    }
                }

                if (mac is not null)
                {
                    foreach (var known in baseHints)
                    {
                        if (!SameMac(known.Mac, mac)) continue;
                        if (localIps.Contains(known.Address)) continue;
                        ips.Add(known.Address);
                    }
                }

                var switchName = JsonString(nic, "SwitchName")?.Trim();
                var category = vmName.Contains("Sandbox", StringComparison.OrdinalIgnoreCase)
                    ? "Windows 沙盒"
                    : "Hyper-V 虚拟机";

                var stateText = LocalizeVmState(state);
                var detail = string.IsNullOrEmpty(stateText)
                    ? $"「{vmName}」"
                    : $"「{vmName}」{stateText}";

                foreach (var ip in ips
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Where(usedIps.Add)
                    .Take(3))
                {
                    hints.Add(new AddressHint(ip, category, switchName, detail, mac));
                }
            }

            return hints;
        }
        catch (TimeoutException)
        {
            LogService.Current?.Info("查询 Hyper-V 虚拟机列表超时，跳过名称识别。");
            return Array.Empty<AddressHint>();
        }
        catch (Exception ex)
        {
            LogService.Current?.Info($"查询 Hyper-V 虚拟机列表跳过：{ex.Message}");
            return Array.Empty<AddressHint>();
        }
    }

    private static IEnumerable<JsonElement> AsArray(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) yield return item;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            // ConvertTo-Json 在某些版本里会把单元素数组序列化成对象
            yield return element;
        }
    }

    private static string? JsonString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            _ => null,
        };
    }

    private static string? LocalizeVmState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        return state.Trim().ToLowerInvariant() switch
        {
            "running" => "运行中",
            "off" => "已关闭",
            "saved" => "已保存",
            "paused" => "已暂停",
            "starting" => "启动中",
            "stopping" => "停止中",
            _ => state.Trim(),
        };
    }

    private static bool IsWindowsSandboxRunning()
    {
        // 旧版是 WindowsSandbox.exe；新版 Sandbox 应用的窗口进程是
        // WindowsSandboxRemoteSession.exe（窗口标题就是 Windows Sandbox）。
        // 用窗口句柄排除只在后台常驻、没有打开沙盒的进程。
        return HasVisibleWindow("WindowsSandbox")
            || HasVisibleWindow("WindowsSandboxClient")
            || HasVisibleWindow("WindowsSandboxRemoteSession");
    }

    private static bool HasVisibleWindow(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch
        {
            return false;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero) return true;
                }
                catch
                {
                    // 进程刚好退出
                }
            }
            return false;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    private static AdapterInfo Classify(NetworkInterface nic)
    {
        var name = nic.Name;
        var desc = nic.Description ?? string.Empty;
        var both = name + " " + desc;
        var label = CleanAdapterName(name, desc);

        if (both.Contains("WSL", StringComparison.OrdinalIgnoreCase))
            return new AdapterInfo(AdapterKind.Wsl, "WSL");

        if (both.Contains("Sandbox", StringComparison.OrdinalIgnoreCase))
            return new AdapterInfo(AdapterKind.Sandbox, label);

        if (both.Contains("VMware", StringComparison.OrdinalIgnoreCase)
            || both.Contains("VMnet", StringComparison.OrdinalIgnoreCase))
            return new AdapterInfo(AdapterKind.VMware, label);

        if (both.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase))
            return new AdapterInfo(AdapterKind.VirtualBox, label);

        if (both.Contains("vEthernet", StringComparison.OrdinalIgnoreCase)
            || both.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
        {
            var kind = label.Contains("Default Switch", StringComparison.OrdinalIgnoreCase)
                ? AdapterKind.DefaultSwitch
                : AdapterKind.HyperV;
            return new AdapterInfo(kind, label);
        }

        if (nic.NetworkInterfaceType is NetworkInterfaceType.Tunnel)
            return new AdapterInfo(AdapterKind.Tunnel, label);

        return new AdapterInfo(AdapterKind.Physical, label);
    }

    /// <summary>把 "vEthernet (Default Switch)" 整理成 "Default Switch"。</summary>
    private static string CleanAdapterName(string name, string description)
    {
        var text = name.Trim();

        if (text.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase))
        {
            var open = text.IndexOf('(');
            var close = text.LastIndexOf(')');
            if (open >= 0 && close > open) text = text[(open + 1)..close].Trim();
        }

        const string vmwarePrefix = "VMware Network Adapter ";
        if (text.StartsWith(vmwarePrefix, StringComparison.OrdinalIgnoreCase))
            text = text[vmwarePrefix.Length..].Trim();

        const string virtualBoxPrefix = "VirtualBox ";
        if (text.StartsWith(virtualBoxPrefix, StringComparison.OrdinalIgnoreCase))
            text = text[virtualBoxPrefix.Length..].Trim();

        if (text.Contains("WSL", StringComparison.OrdinalIgnoreCase)) text = "WSL";
        if (text.Length == 0) text = description.Trim();

        return text;
    }

    private static string? NormalizeMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var hex = new string(mac.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 12 ? hex : null;
    }

    private static bool SameMac(string? a, string? b)
    {
        var left = NormalizeMac(a);
        var right = NormalizeMac(b);
        return left is not null && right is not null && left == right;
    }

    private static string? MacVendor(string mac)
    {
        var hex = NormalizeMac(mac);
        if (hex is null) return null;

        return hex[..6] switch
        {
            "00155D" => "微软虚拟网卡",
            "005056" or "000C29" or "000569" or "001C14" => "VMware 虚拟网卡",
            "080027" or "0A0027" => "VirtualBox 虚拟网卡",
            "525400" => "QEMU/KVM 虚拟网卡",
            "00163E" => "Xen 虚拟网卡",
            "0003FF" => "Hyper-V/虚拟 PC 网卡",
            "001C42" => "Parallels 虚拟网卡",
            _ => null,
        };
    }

    private static bool InSubnet(string address, string networkAddress, int prefixLength)
    {
        if (!IPAddress.TryParse(address, out var ip)) return false;
        if (!IPAddress.TryParse(networkAddress, out var net)) return false;

        var a = ip.GetAddressBytes();
        var b = net.GetAddressBytes();
        if (a.Length != 4 || b.Length != 4) return false;

        prefixLength = Math.Clamp(prefixLength, 0, 32);
        var full = prefixLength / 8;
        for (var i = 0; i < full; i++)
            if (a[i] != b[i]) return false;

        var remainder = prefixLength % 8;
        if (remainder == 0) return true;

        var mask = (byte)(0xFF << (8 - remainder));
        return (a[full] & mask) == (b[full] & mask);
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
