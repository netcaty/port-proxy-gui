using System.Net;
using System.Net.Sockets;
using System.Text;
using PortProxyManager.Models;

namespace PortProxyManager.Services;

public sealed class SelfTestStep
{
    public required string Name { get; init; }
    public bool Ok { get; set; }
    public bool Skipped { get; set; }
    public string? Detail { get; set; }
}

public sealed class SelfTestReport
{
    public string StartedAt { get; set; } = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz");
    public bool Elevated { get; set; }
    public string? DataDir { get; set; }
    public string? DataDirSource { get; set; }
    public List<SelfTestStep> Steps { get; set; } = new();

    public bool Ok => Steps.Where(s => !s.Skipped).All(s => s.Ok);
    public int Passed => Steps.Count(s => !s.Skipped && s.Ok);
    public int Failed => Steps.Count(s => !s.Skipped && !s.Ok);
    public int SkippedCount => Steps.Count(s => s.Skipped);
}

/// <summary>
/// 自检。只读部分（注册表解析、校验规则、配置落盘）任何身份都能跑；
/// 写入部分需要管理员，会先加一条高位端口的临时映射，核对后再删掉，
/// 并在最后比对测试前后的完整快照，确保没有污染现有配置。
/// </summary>
public static class SelfTest
{
    public static int Run(CliOptions options, bool writePhase)
    {
        Cli.EnsureConsole();

        var report = new SelfTestReport
        {
            Elevated = Elevation.IsElevated,
        };

        var paths = PathContext.Resolve(options.DataDir, out var dirError);
        report.DataDir = paths?.DataDir;
        report.DataDirSource = paths?.SourceLabel;

        var entriesBefore = RegistryReader.ReadAll(out var readError);
        var snapshotBefore = Snapshot(entriesBefore);

        // ---------- 只读部分 ----------
        Add(report, "读取注册表映射",
            readError is null,
            readError ?? $"解析到 {entriesBefore.Count} 条映射：" +
                        (entriesBefore.Count == 0
                            ? "（当前没有任何映射）"
                            : string.Join("；", entriesBefore.Select(e => $"{e.Family} {e.ListenText}→{e.ConnectText}"))));

        Add(report, "映射唯一键可构造",
            entriesBefore.All(e => !string.IsNullOrWhiteSpace(e.Key) && e.Key.Count(c => c == '|') == 3),
            entriesBefore.Count == 0 ? "无映射可检查（视为通过）" : null);

        Add(report, "地址与端口校验规则", ValidateRules(entriesBefore));

        Add(report, "数据目录可用",
            paths is not null,
            paths is not null
                ? $"{paths.DataDir}（{paths.SourceLabel}）"
                : $"不可用：{dirError}");

        if (paths is not null)
        {
            Add(report, "meta.json 读写往返", TestMetaRoundTrip(paths));
            Add(report, "settings.json 读写往返", TestSettingsRoundTrip(paths));
        }
        else
        {
            Skip(report, "meta.json 读写往返", "数据目录不可用");
            Skip(report, "settings.json 读写往返", "数据目录不可用");
        }

        // ---------- 写入部分 ----------
        var ipplp = IpHelperService.GetState();
        Add(report, "IP Helper 服务状态",
            ipplp == IpHlpState.Running,
            ipplp switch
            {
                IpHlpState.Running => "运行中",
                IpHlpState.Stopped => "已停止——映射不会生效",
                IpHlpState.NotFound => "系统里没有 iphlpsvc",
                _ => "状态未知",
            });

        if (!writePhase)
        {
            const string reason = "未提权，跳过写入测试（用 --selftest 并确认 UAC 即可完整执行）";
            Skip(report, "新增临时映射", reason);
            Skip(report, "写入后核对", reason);
            Skip(report, "重复新增应被拒绝", reason);
            Skip(report, "删除临时映射", reason);
            Skip(report, "原配置未受影响", reason);
        }
        else
        {
            RunWritePhase(report, entriesBefore);
        }

        // ---------- 报告 ----------
        var entriesAfter = RegistryReader.ReadAll(out _);
        var unchanged = Snapshot(entriesAfter).SequenceEqual(snapshotBefore);
        Add(report, "测试后快照比对", unchanged,
            writePhase
                ? $"测试前 {snapshotBefore.Count} 条，测试后 {entriesAfter.Count} 条" +
                  (unchanged ? "，内容一致" : "，内容不一致！")
                : $"只读测试未改动任何内容（{entriesAfter.Count} 条）");

        var reportPath = WriteReport(paths, report);

        PrintSummary(report, reportPath);
        return report.Ok ? 0 : 1;
    }

    private static void RunWritePhase(SelfTestReport report, IReadOnlyList<PortProxyEntry> before)
    {
        var service = new PortProxyService();
        var port = FindFreePort();
        if (port == 0)
        {
            const string reason = "18080–18120 之间没有找到空闲端口，已跳过写入测试";
            Skip(report, "新增临时映射", reason);
            Skip(report, "写入后核对", reason);
            Skip(report, "重复新增应被拒绝", reason);
            Skip(report, "删除临时映射", reason);
            return;
        }

        var probe = new PortProxyEntry
        {
            Family = "v4tov4",
            Protocol = "tcp",
            ListenAddress = "127.0.0.1",
            ListenPort = port,
            ConnectAddress = "127.0.0.1",
            ConnectPort = 9,
        };

        var added = false;
        try
        {
            try
            {
                service.Add(probe);
                added = true;
                Add(report, "新增临时映射", true, $"{probe.ListenText} → {probe.ConnectText}");
            }
            catch (Exception ex)
            {
                Add(report, "新增临时映射", false, ex.Message);
            }

            // 写入后核对：不信任 Add 的返回值，独立读一次注册表
            var afterAdd = RegistryReader.ReadAll(out _);
            var found = afterAdd.FirstOrDefault(e => e.Key.Equals(probe.Key, StringComparison.OrdinalIgnoreCase));
            Add(report, "写入后核对", found is not null,
                found is null ? "注册表里查不到刚写入的映射" : $"注册表已确认：{found.ListenText} → {found.ConnectText}");

            // 重复新增必须失败，否则说明唯一性校验有漏洞
            if (added)
            {
                try
                {
                    service.Add(probe);
                    Add(report, "重复新增应被拒绝", false, "netsh 居然接受了重复的监听地址和端口");
                }
                catch (PortProxyException)
                {
                    Add(report, "重复新增应被拒绝", true, "已按预期拒绝");
                }
                catch (Exception ex)
                {
                    Add(report, "重复新增应被拒绝", false, $"抛出了非预期异常：{ex.Message}");
                }
            }
            else
            {
                Skip(report, "重复新增应被拒绝", "前置步骤失败，无法判断");
            }

            try
            {
                service.Delete(probe);
                var afterDelete = RegistryReader.ReadAll(out _);
                var stillThere = afterDelete.Any(e => e.Key.Equals(probe.Key, StringComparison.OrdinalIgnoreCase));
                Add(report, "删除临时映射", !stillThere,
                    stillThere ? "删除后映射仍然存在" : "已删除");
            }
            catch (Exception ex)
            {
                Add(report, "删除临时映射", false, ex.Message);
            }
        }
        finally
        {
            // 无论如何都要把痕迹清掉
            try
            {
                var leftover = RegistryReader.ReadAll(out _)
                    .FirstOrDefault(e => e.Key.Equals(probe.Key, StringComparison.OrdinalIgnoreCase));
                if (leftover is not null)
                {
                    LogService.Current?.Warn("自检清理：发现残留映射，正在删除");
                    service.Delete(leftover);
                }
            }
            catch (Exception ex)
            {
                LogService.Current?.Error($"自检清理失败：{ex.Message}");
            }
        }

        var finalEntries = RegistryReader.ReadAll(out _);
        var untouched = Snapshot(finalEntries).SequenceEqual(Snapshot(before));
        Add(report, "原配置未受影响", untouched,
            $"测试前 {before.Count} 条 / 测试后 {finalEntries.Count} 条" +
            (untouched ? "，逐条一致" : "，存在差异！"));
    }

    private static bool ValidateRules(IReadOnlyList<PortProxyEntry> existing)
    {
        PortProxyEntry Make(string family, string listen, int listenPort, string connect, int connectPort) => new()
        {
            Family = family,
            Protocol = "tcp",
            ListenAddress = listen,
            ListenPort = listenPort,
            ConnectAddress = connect,
            ConnectPort = connectPort,
        };

        var cases = new List<(string Name, PortProxyEntry Entry, string? EditingKey, bool ExpectOk)>
        {
            ("合法 v4tov4", Make("v4tov4", "127.0.0.1", 18080, "127.0.0.1", 8080), null, true),
            ("监听端口 0", Make("v4tov4", "127.0.0.1", 0, "127.0.0.1", 8080), null, false),
            ("监听端口 65536", Make("v4tov4", "127.0.0.1", 65536, "127.0.0.1", 8080), null, false),
            ("监听地址非法", Make("v4tov4", "abc", 18080, "127.0.0.1", 8080), null, false),
            ("监听地址为空（视为所有地址）", Make("v4tov4", "", 18080, "127.0.0.1", 8080), null, true),
            ("目标地址为通配", Make("v4tov4", "127.0.0.1", 18080, "*", 8080), null, false),
            ("v4 侧填 IPv6", Make("v4tov4", "::1", 18080, "127.0.0.1", 8080), null, false),
            ("目标端口 0", Make("v4tov4", "127.0.0.1", 18080, "127.0.0.1", 0), null, false),
            ("合法 v6tov4", Make("v6tov4", "::", 18080, "127.0.0.1", 8080), null, true),
        };

        // 用真实存在的条目测重复检查（只读，不会写任何东西）
        var sample = existing.FirstOrDefault();
        if (sample is not null)
        {
            cases.Add(("重复监听应被拒绝", sample, null, false));
            cases.Add(("编辑自身不算重复", sample, sample.Key, true));
        }

        var failures = new List<string>();
        foreach (var (name, entry, editingKey, expectOk) in cases)
        {
            var result = Validator.Validate(entry, existing, editingKey);
            if (result.Ok != expectOk)
                failures.Add($"{name}：期望 {(expectOk ? "通过" : "拒绝")}，实际 {(result.Ok ? "通过" : "拒绝")}");
        }

        if (failures.Count > 0)
        {
            Console.WriteLine("校验规则失败项：" + string.Join("；", failures));
            return false;
        }

        return true;
    }

    private static bool TestMetaRoundTrip(PathContext paths)
    {
        const string testKey = "__selftest__|tcp|127.0.0.1|1";
        try
        {
            var store = new MetaStore(paths.MetaFile);
            store.Set(testKey, new EntryMeta
            {
                Name = "自检条目",
                Group = "自检",
                Note = "如果看到这条说明清理失败",
            });
            store.Save();

            var reloaded = new MetaStore(paths.MetaFile);
            var meta = reloaded.Get(testKey);
            var ok = meta?.Name == "自检条目" && meta.Group == "自检";

            reloaded.Remove(testKey);
            reloaded.Save();

            var cleaned = new MetaStore(paths.MetaFile).Get(testKey) is null;
            return ok && cleaned;
        }
        catch (Exception ex)
        {
            LogService.Current?.Error($"meta.json 往返测试失败：{ex.Message}");
            return false;
        }
    }

    private static bool TestSettingsRoundTrip(PathContext paths)
    {
        var file = Path.Combine(paths.DataDir, "settings.selftest.json");
        try
        {
            var settings = new AppSettings { WindowWidth = 1234, LastGroup = "自检分组" };
            JsonIo.Write(file, settings);
            var back = JsonIo.Read<AppSettings>(file);
            var ok = back?.WindowWidth == 1234 && back.LastGroup == "自检分组";
            File.Delete(file);
            return ok;
        }
        catch (Exception ex)
        {
            LogService.Current?.Error($"settings.json 往返测试失败：{ex.Message}");
            return false;
        }
    }

    private static int FindFreePort()
    {
        var busy = new HashSet<int>(RegistryReader.ActiveTcpListeners().Select(l => l.Port));

        for (var port = 18080; port <= 18120; port++)
        {
            if (busy.Contains(port)) continue;
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch
            {
                // 被占用，试下一个
            }
        }

        return 0;
    }

    private static List<string> Snapshot(IEnumerable<PortProxyEntry> entries) =>
        entries.Select(e => $"{e.Key}=>{e.NormalizedConnect}:{e.ConnectPort}")
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static void Add(SelfTestReport report, string name, bool ok, string? detail = null)
        => report.Steps.Add(new SelfTestStep { Name = name, Ok = ok, Detail = detail });

    private static void Skip(SelfTestReport report, string name, string detail)
        => report.Steps.Add(new SelfTestStep { Name = name, Skipped = true, Detail = detail });

    private static string? WriteReport(PathContext? paths, SelfTestReport report)
    {
        var candidates = new List<string>();
        if (paths is not null) candidates.Add(paths.SelfTestReport);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "selftest-report.json"));
        candidates.Add(Path.Combine(Path.GetTempPath(), "portproxy-selftest-report.json"));

        foreach (var candidate in candidates)
        {
            try
            {
                JsonIo.Write(candidate, report);
                return candidate;
            }
            catch
            {
                // 换下一个位置
            }
        }

        return null;
    }

    private static void PrintSummary(SelfTestReport report, string? reportPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("== 自检结果 ==");
        sb.AppendLine($"身份：{(report.Elevated ? "管理员" : "普通用户")}    数据目录：{report.DataDir ?? "不可用"}");

        foreach (var step in report.Steps)
        {
            var mark = step.Skipped ? "SKIP" : step.Ok ? " OK " : "FAIL";
            sb.AppendLine($"[{mark}] {step.Name}{(string.IsNullOrWhiteSpace(step.Detail) ? "" : "：" + step.Detail)}");
        }

        sb.AppendLine();
        sb.AppendLine($"通过 {report.Passed} / 失败 {report.Failed} / 跳过 {report.SkippedCount}");
        sb.AppendLine(report.Ok ? "结论：自检通过" : "结论：存在失败项");
        if (reportPath is not null) sb.AppendLine($"报告：{reportPath}");

        Cli.SafeWrite(sb.ToString());
    }
}
