namespace PortProxyManager.Services;

public enum DataDirSource
{
    /// <summary>--data-dir 显式指定。</summary>
    CommandLine,
    /// <summary>exe 同目录（默认，绿色便携）。</summary>
    ExeDirectory,
    /// <summary>回退到 ProgramData。</summary>
    ProgramData,
}

/// <summary>
/// 数据目录解析。优先级：--data-dir → exe 同目录 → %ProgramData%。
/// 显式指定失败时直接报错退出，绝不静默改道。
/// </summary>
public sealed class PathContext
{
    public required string DataDir { get; init; }
    public required DataDirSource Source { get; init; }
    public string? FallbackReason { get; init; }

    public string MetaFile => Path.Combine(DataDir, "meta.json");
    public string SettingsFile => Path.Combine(DataDir, "settings.json");
    public string LogDir => Path.Combine(DataDir, "logs");
    public string BackupDir => Path.Combine(DataDir, "backups");
    public string SelfTestReport => Path.Combine(DataDir, "selftest-report.json");

    public string SourceLabel => Source switch
    {
        DataDirSource.CommandLine => "--data-dir 指定",
        DataDirSource.ExeDirectory => "exe 同目录",
        DataDirSource.ProgramData => "ProgramData（已降级）",
        _ => Source.ToString(),
    };

    public void EnsureLayout()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(BackupDir);
    }

    /// <summary>
    /// 解析数据目录。失败时返回 null 并给出可直接展示给用户的中文原因。
    /// </summary>
    public static PathContext? Resolve(string? requested, out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            string full;
            try
            {
                // 相对路径按 exe 目录解析，避免依赖启动时的工作目录
                full = Path.GetFullPath(requested.Trim().Trim('"'), AppContext.BaseDirectory);
            }
            catch (Exception ex)
            {
                error = $"数据目录路径无效：{requested}\n{ex.Message}";
                return null;
            }

            if (!TryPrepare(full, out var reason))
            {
                error = $"指定的数据目录不可用：{full}\n原因：{reason}\n" +
                        "（显式指定的路径不会自动降级到其他位置，请检查权限或换一个路径）";
                return null;
            }

            return new PathContext { DataDir = full, Source = DataDirSource.CommandLine };
        }

        var exeDir = AppContext.BaseDirectory;

        string exeReason;
        if (IsSystemProtectedPath(exeDir))
        {
            exeReason = "exe 位于系统保护目录（Program Files / Windows），不适合存放用户数据";
        }
        else if (TryPrepare(exeDir, out var r1))
        {
            return new PathContext { DataDir = exeDir, Source = DataDirSource.ExeDirectory };
        }
        else
        {
            exeReason = r1;
        }

        var programData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PortProxyManager");

        if (TryPrepare(programData, out var r2))
        {
            return new PathContext
            {
                DataDir = programData,
                Source = DataDirSource.ProgramData,
                FallbackReason = exeReason,
            };
        }

        error = $"无法确定可用的数据目录。\n" +
                $"· exe 同目录：{exeReason}\n" +
                $"· ProgramData：{r2}\n" +
                "请用 --data-dir 指定一个可写目录。";
        return null;
    }

    /// <summary>
    /// 只读场景（--dump）用：只算出路径，不建目录、不写探测文件。
    /// </summary>
    public static PathContext? ResolveForRead(string? requested, out string? error)
    {
        error = null;

        if (!string.IsNullOrWhiteSpace(requested))
        {
            try
            {
                var full = Path.GetFullPath(requested.Trim().Trim('"'), AppContext.BaseDirectory);
                return new PathContext { DataDir = full, Source = DataDirSource.CommandLine };
            }
            catch (Exception ex)
            {
                error = $"数据目录路径无效：{requested}（{ex.Message}）";
                return null;
            }
        }

        var exeDir = AppContext.BaseDirectory;
        if (!IsSystemProtectedPath(exeDir))
            return new PathContext { DataDir = exeDir, Source = DataDirSource.ExeDirectory };

        return new PathContext
        {
            DataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "PortProxyManager"),
            Source = DataDirSource.ProgramData,
            FallbackReason = "exe 位于系统保护目录",
        };
    }

    /// <summary>建目录并实测可写（建临时文件再删），不靠 ACL 推断。</summary>
    public static bool TryPrepare(string dir, out string reason)
    {
        reason = string.Empty;
        try
        {
            Directory.CreateDirectory(dir);

            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}.tmp");
            using (var fs = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.WriteByte(0);
            }
            File.Delete(probe);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            reason = "目录不存在且无法创建";
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "没有写入权限";
            return false;
        }
        catch (IOException ex)
        {
            reason = $"写入测试失败：{ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    private static bool IsSystemProtectedPath(string dir)
    {
        var full = dir.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.Windows,
                 })
        {
            var root = Environment.GetFolderPath(special);
            if (string.IsNullOrEmpty(root)) continue;

            root = root.TrimEnd(Path.DirectorySeparatorChar);
            if (full.Equals(root, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
