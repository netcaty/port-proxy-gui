using System.Text.RegularExpressions;

namespace PortProxyManager.Services;

/// <summary>
/// 查询 Windows 的 TCP 端口排除区间。
///
/// 这些区间由 Hyper-V / WSL / WinNAT 等在启动时占用（"管理的端口排除"），
/// 落在区间内的端口**任何程序都无法绑定**——portproxy 也一样，表现为
/// 映射配置存在、页面显示正常、服务也在跑，但监听器永远建不起来。
///
/// 系统没有公开的查询 API，只能解析 netsh 的输出；数据行形如
///   开始端口    结束端口
///       3390        3489
/// 该格式与系统语言无关，只按"两个数字"匹配即可。
/// </summary>
public static class PortReservationService
{
    private static readonly Regex RangeRow = new(
        @"^\s*(\d{1,5})\s+(\d{1,5})\s*\*?\s*$",
        RegexOptions.Compiled);

    private static readonly object Gate = new();
    private static (int Start, int End)[] _ranges = Array.Empty<(int, int)>();
    private static DateTime _cachedAtUtc = DateTime.MinValue;
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);

    public static IReadOnlyList<(int Start, int End)> GetExcludedTcpRanges(bool refresh = false)
    {
        lock (Gate)
        {
            if (!refresh && DateTime.UtcNow - _cachedAtUtc < Ttl)
                return _ranges;
        }

        var ranges = Query();

        lock (Gate)
        {
            _ranges = ranges;
            _cachedAtUtc = DateTime.UtcNow;
        }

        return ranges;
    }

    /// <summary>返回包含该端口的保留区间；不在任何区间内则返回 null。</summary>
    public static (int Start, int End)? FindContaining(int port)
    {
        foreach (var range in GetExcludedTcpRanges())
        {
            if (port >= range.Start && port <= range.End) return range;
        }

        return null;
    }

    private static (int, int)[] Query()
    {
        try
        {
            var netsh = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

            var result = ProcRunner.Run(
                netsh, new[] { "int", "ipv4", "show", "excludedportrange", "protocol=tcp" },
                timeoutMs: 6000);

            if (!result.Ok) return Array.Empty<(int, int)>();

            var ranges = new List<(int, int)>();
            foreach (var raw in result.StdOut.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var match = RangeRow.Match(raw);
                if (!match.Success) continue;

                if (!int.TryParse(match.Groups[1].Value, out var start)) continue;
                if (!int.TryParse(match.Groups[2].Value, out var end)) continue;
                if (start <= 0 || end < start) continue;

                ranges.Add((start, end));
            }

            return ranges.ToArray();
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"查询端口排除区间失败：{ex.Message}");
            return Array.Empty<(int, int)>();
        }
    }
}
