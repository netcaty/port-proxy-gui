namespace PortProxyManager.Services;

/// <summary>
/// netsh 的封装。所有映射写入的唯一通道。
/// 退出码 0 不代表真的生效，调用方必须再读注册表核对（见 PortProxyService）。
/// </summary>
public static class NetshRunner
{
    private static readonly string NetshPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "netsh.exe");

    public static ProcResult Run(params string[] args) => Run((IReadOnlyList<string>)args);

    public static ProcResult Run(IReadOnlyList<string> args)
    {
        var display = "netsh " + string.Join(" ", args.Select(QuoteForDisplay));
        ProcResult result;
        try
        {
            result = ProcRunner.Run(NetshPath, args);
        }
        catch (Exception ex)
        {
            LogService.Current?.Error($"{display} 执行失败：{ex.Message}");
            throw new PortProxyException($"无法执行 netsh：{ex.Message}");
        }

        if (result.Ok)
        {
            LogService.Current?.Info($"{display} -> exit 0");
        }
        else
        {
            LogService.Current?.Error(
                $"{display} -> exit {result.ExitCode}{Environment.NewLine}{result.Combined}");
        }

        return result;
    }

    /// <summary>
    /// 把 netsh 的原始报错翻成用户能看懂的话，并保留原文方便排查。
    /// netsh 消息随系统语言变化，所以中英关键词都匹配。
    /// </summary>
    public static string Friendly(ProcResult result)
    {
        var raw = result.Combined.Trim();

        if (raw.Length == 0)
            return $"netsh 执行失败（退出码 {result.ExitCode}），没有返回任何信息。";

        string? hint = null;

        if (Contains(raw, "已存在", "already exists", "对象已存在", "object already exists"))
            hint = "该监听地址和端口已经有一条映射了。";
        else if (Contains(raw, "拒绝访问", "Access is denied", "请求的操作需要提升", "requires elevation"))
            hint = "权限不足，需要以管理员身份运行。";
        else if (Contains(raw, "另一个程序", "in use", "被占用", "占用"))
            hint = "该端口正被其他程序占用。";
        else if (Contains(raw, "参数", "parameter", "invalid", "不正确"))
            hint = "参数无效，请检查地址与端口。";
        else if (Contains(raw, "没有与指定", "not found", "找不到", "不存在"))
            hint = "找不到指定的映射（可能已被其他程序删除）。";

        return hint is null ? raw : $"{hint}{Environment.NewLine}（原始信息：{raw}）";
    }

    private static bool Contains(string text, params string[] keywords) =>
        keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static string QuoteForDisplay(string arg) =>
        arg.Contains(' ') ? $"\"{arg}\"" : arg;
}
