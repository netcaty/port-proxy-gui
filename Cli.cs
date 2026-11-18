using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using PortProxyManager.Services;

namespace PortProxyManager;

public static class Cli
{
    private const int AttachParentProcess = -1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    /// <summary>WinExe 没有自己的控制台，从命令行启动时挂到父进程的控制台上。</summary>
    public static void EnsureConsole()
    {
        try
        {
            if (Console.IsOutputRedirected) return;
            if (GetConsoleWindow() != IntPtr.Zero) return;
            AttachConsole(AttachParentProcess);
        }
        catch
        {
            // 没有控制台也无所谓，仍然可以用 --out 输出到文件
        }
    }

    private static void PrepareEncoding()
    {
        try
        {
            if (Console.IsOutputRedirected)
                Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 编码设置失败不影响文本生成
        }
    }

    public static void WriteHelp()
    {
        EnsureConsole();
        PrepareEncoding();

        const string text = """
            PortProxy 管理器 — netsh portproxy 的图形界面封装

            用法:
              PortProxyManager.exe [选项]

            选项:
              --data-dir=<路径>   指定数据目录（meta.json / settings.json / logs / backups）。
                                  相对路径按 exe 所在目录解析。显式指定后不会自动降级。
                                  不指定时优先用 exe 同目录，不可写则回退到
                                  %ProgramData%\PortProxyManager。
              --tray              启动后直接最小化到托盘。
              --dump              以文本输出当前全部映射后退出（只读，不需要管理员）。
              --hints             探测并输出全部目标地址候选后退出（只读，不需要管理员）。
                                  包含 WSL / 虚拟机 / 沙盒 / 网关 / 局域网设备。
              --json              与 --dump / --hints 一起使用，输出 JSON。
              --out=<文件>        与 --dump / --hints 一起使用，同时把结果写入文件。
              --selftest          运行自检（读取、校验、增删核对），输出 selftest-report.json。
                                  未提权时会先尝试提权；取消 UAC 则只跑只读部分。
              --no-elevate        不自动提权（GUI 只读，写入会提示需要管理员权限）。
              -h, --help          显示本帮助。

            说明:
              · 映射的唯一数据源是注册表 HKLM\SYSTEM\CurrentControlSet\Services\PortProxy，
                写入一律通过 netsh，不会直接改注册表。
              · netsh portproxy 只支持 TCP，UDP 转发无法实现。
              · 映射本身重启后由 IP Helper 自动恢复，无需守护进程。
            """;

        SafeWriteLine(text);
    }

    public static int Dump(CliOptions options)
    {
        PrepareEncoding();

        var entries = RegistryReader.ReadAll(out var readError);
        var paths = PathContext.ResolveForRead(options.DataDir, out var dirError);
        // --dump 是纯只读操作：不创建目录、不写任何文件，只尝试读取已有的 meta.json

        var meta = paths is null ? null : new MetaStore(paths.MetaFile);
        var sb = new StringBuilder();

        if (options.Json)
        {
            var payload = new
            {
                ok = readError is null,
                error = readError,
                elevated = Elevation.IsElevated,
                dataDir = paths?.DataDir,
                dataDirSource = paths?.Source.ToString(),
                dataDirError = dirError,
                entries = entries.Select(e => new
                {
                    family = e.Family,
                    protocol = e.Protocol,
                    listenAddress = e.NormalizedListen,
                    listenPort = e.ListenPort,
                    connectAddress = e.NormalizedConnect,
                    connectPort = e.ConnectPort,
                    key = e.Key,
                    name = meta?.Get(e.Key)?.Name,
                    group = meta?.Get(e.Key)?.Group,
                    note = meta?.Get(e.Key)?.Note,
                }),
            };

            sb.AppendLine(JsonIo.Serialize(payload));
        }
        else
        {
            sb.AppendLine("PortProxy 映射");
            sb.AppendLine($"数据来源：注册表 HKLM\\{RegistryReader.RootPath}\\<family>\\tcp");
            sb.AppendLine(paths is null
                ? $"数据目录：不可用（{dirError}）"
                : $"数据目录：{paths.DataDir}（来源：{paths.SourceLabel}）");
            sb.AppendLine($"当前权限：{(Elevation.IsElevated ? "管理员" : "普通用户（写入需要提权）")}");

            if (readError is not null)
                sb.AppendLine($"读取错误：{readError}");

            sb.AppendLine();

            var names = entries.Select(e => meta?.Get(e.Key)).ToList();
            var rows = new List<string[]>
            {
                new[] { "类型", "协议", "监听", "目标", "名称" },
            };

            for (var i = 0; i < entries.Count; i++)
            {
                rows.Add(new[]
                {
                    entries[i].Family,
                    entries[i].Protocol,
                    entries[i].ListenText,
                    entries[i].ConnectText,
                    names[i]?.Name ?? string.Empty,
                });
            }

            var widths = new int[5];
            foreach (var row in rows)
                for (var c = 0; c < 5; c++)
                    widths[c] = Math.Max(widths[c], DisplayWidth(row[c]));

            foreach (var row in rows)
            {
                var line = new StringBuilder();
                for (var c = 0; c < 5; c++)
                    line.Append(Pad(row[c], widths[c] + 2));
                sb.AppendLine(line.ToString().TrimEnd());
            }

            sb.AppendLine();
            sb.AppendLine($"共 {entries.Count} 条");
        }

        var output = sb.ToString();
        SafeWrite(output);

        if (!string.IsNullOrWhiteSpace(options.OutFile))
        {
            try
            {
                // 输出文件按当前工作目录解析，符合命令行工具的惯例
                // （数据目录则相反，必须锚定 exe 目录）
                var full = Path.GetFullPath(options.OutFile!);
                File.WriteAllText(full, output, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                SafeWriteLine($"写入 --out 文件失败：{ex.Message}");
                return 4;
            }
        }

        return readError is null ? 0 : 4;
    }

    /// <summary>
    /// 探测全部目标地址候选并输出（WSL / 虚拟机 / 沙盒 / 网关 / 局域网设备）。
    /// 只读、不需要管理员、不碰界面，也不受单实例限制，可与 GUI 同时运行。
    /// </summary>
    public static async Task<int> Hints(CliOptions options)
    {
        EnsureConsole();
        PrepareEncoding();

        IReadOnlyList<AddressHint> hints;
        string? probeError = null;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            hints = await GuestHintCache.RefreshNowAsync(force: true);
        }
        catch (Exception ex)
        {
            probeError = ex.Message;
            hints = Array.Empty<AddressHint>();
        }

        var elapsedMs = stopwatch.ElapsedMilliseconds;

        var sb = new StringBuilder();

        if (options.Json)
        {
            var payload = new
            {
                ok = probeError is null,
                error = probeError,
                elevated = Elevation.IsElevated,
                count = hints.Count,
                elapsedMs,
                candidates = hints.Select(h => new
                {
                    kind = h.Kind,
                    address = h.Address,
                    category = h.Category,
                    id = h.Id,
                    mac = h.Mac,
                    source = h.Source,
                    detail = h.Detail,
                    display = h.Display,
                }),
            };

            sb.AppendLine(JsonIo.Serialize(payload));
        }
        else
        {
            sb.AppendLine("PortProxy 目标地址候选");
            sb.AppendLine($"探测耗时：{elapsedMs} ms");
            sb.AppendLine($"当前权限：{(Elevation.IsElevated ? "管理员" : "普通用户")}");

            if (probeError is not null)
                sb.AppendLine($"探测错误：{probeError}");

            sb.AppendLine();

            var rows = new List<string[]> { new[] { "类型", "地址", "标识/名称", "MAC", "说明" } };

            foreach (var hint in hints)
            {
                var note = string.Join(" · ", new[] { hint.Category, hint.Source, hint.Detail }
                    .Where(p => !string.IsNullOrWhiteSpace(p)));

                rows.Add(new[]
                {
                    hint.Kind ?? "?",
                    hint.Address,
                    hint.Id ?? string.Empty,
                    hint.Mac ?? string.Empty,
                    note,
                });
            }

            var widths = new int[5];
            foreach (var row in rows)
                for (var c = 0; c < 5; c++)
                    widths[c] = Math.Max(widths[c], DisplayWidth(row[c]));

            foreach (var row in rows)
            {
                var line = new StringBuilder();
                for (var c = 0; c < 5; c++)
                    line.Append(Pad(row[c], widths[c] + 2));
                sb.AppendLine(line.ToString().TrimEnd());
            }

            sb.AppendLine();
            sb.AppendLine($"共 {hints.Count} 条");
        }

        var output = sb.ToString();
        SafeWrite(output);

        if (!string.IsNullOrWhiteSpace(options.OutFile))
        {
            try
            {
                // 输出文件按当前工作目录解析，与 --dump 一致
                var full = Path.GetFullPath(options.OutFile!);
                File.WriteAllText(full, output, new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                SafeWriteLine($"写入 --out 文件失败：{ex.Message}");
                return 4;
            }
        }

        return probeError is null ? 0 : 5;
    }

    /// <summary>以管理员身份重启自身。返回子进程退出码；用户取消 UAC 时返回 1223。</summary>
    public static int RelaunchElevated(string[] args, bool wait)
    {
        var exe = AutorunService.ExecutablePath;

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory,
        };

        foreach (var arg in args) psi.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return 1;

            if (!wait) return 0;

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return 1223;
        }
        catch (Exception ex)
        {
            SafeWriteLine($"提权启动失败：{ex.Message}");
            return 1;
        }
    }

    public static void SafeWrite(string text)
    {
        try
        {
            Console.Out.Write(text);
            Console.Out.Flush();
        }
        catch
        {
            // 没有可用输出流时放弃
        }
    }

    public static void SafeWriteLine(string text) => SafeWrite(text + Environment.NewLine);

    public static void ErrorBox(string message)
    {
        try
        {
            MessageBox.Show(message, "PortProxy 管理器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch
        {
            SafeWriteLine(message);
        }
    }

    private static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var ch in text) width += ch >= 0x2E80 ? 2 : 1;
        return width;
    }

    private static string Pad(string text, int width) =>
        text + new string(' ', Math.Max(1, width - DisplayWidth(text)));
}
