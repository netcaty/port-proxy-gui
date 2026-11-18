using System.Collections.ObjectModel;
using System.Net.NetworkInformation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PortProxyManager.Models;
using PortProxyManager.Services;
using PortProxyManager.WinUI.Views;

namespace PortProxyManager.WinUI.ViewModels;

/// <summary>
/// 主窗口状态机。旧版 MainForm 的数据流：
/// 刷新 → 行列表 → 分组/过滤/排序 → 表格；RegistryWatcher/网络事件切回 UI 线程后刷新。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public const string AllGroupsLabel = "全部分组";

    private readonly PortProxyService _service = new();

    private List<EntryRowViewModel> _rows = new();
    private string? _sortProperty;
    private bool _sortAscending = true;
    private bool _refreshing;

    private RegistryWatcher? _watcher;
    private IpHlpState _ipState = IpHlpState.Unknown;
    private IReadOnlyList<PortProxyEntry> _deadMappings = Array.Empty<PortProxyEntry>();
    private Action? _bannerAction;

    /// <summary>当前显示的行（过滤+排序后）。</summary>
    public ObservableCollection<EntryRowViewModel> View { get; } = new();

    /// <summary>分组下拉的全部选项。</summary>
    public ObservableCollection<string> GroupOptions { get; } = new();

    [ObservableProperty]
    public partial string FilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedGroup { get; set; } = AllGroupsLabel;

    // 过滤/分组变化 → 重算视图（ApplyFilter 内会把偏好写入 settings.json）
    partial void OnFilterTextChanged(string value)
    {
        if (value is null) return;
        ApplyFilter();
    }

    partial void OnSelectedGroupChanged(string value)
    {
        // 分组集合重建过程中 ComboBox 会短暂回写 null，此时跳过
        if (value is null) return;
        ApplyFilter();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanModifySelected))]
    public partial EntryRowViewModel? SelectedRow { get; set; }

    [ObservableProperty]
    public partial string CountText { get; set; } = "共 0 条";

    [ObservableProperty]
    public partial string HelperText { get; set; } = "IP Helper：检测中";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HelperBrush))]
    public partial bool HelperIsRunning { get; set; }

    [ObservableProperty]
    public partial string DirText { get; set; } = string.Empty;

    /// <summary>编辑/删除按钮：需要选中行且已提权。</summary>
    public bool CanModifySelected => SelectedRow is not null && AppRuntime.Elevated;

    private static readonly Brush HelperOkBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x1B, 0x5E, 0x20));

    private static readonly Brush HelperBadBrush =
        new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xA6, 0x1B, 0x1B));

    /// <summary>IP Helper 状态文本的颜色。</summary>
    public Brush HelperBrush => HelperIsRunning ? HelperOkBrush : HelperBadBrush;

    [RelayCommand]
    private void Refresh() => RefreshData();

    // ---- InfoBar 横幅（对应旧版 _banner）----

    [ObservableProperty]
    public partial bool BannerVisible { get; set; }

    [ObservableProperty]
    public partial string BannerMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BannerActionVisibility))]
    public partial string BannerActionText { get; set; } = string.Empty;

    /// <summary>横幅动作按钮：文案为空时隐藏。</summary>
    public Visibility BannerActionVisibility =>
        BannerActionText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>窗口就绪后由 MainWindow 调用：启动监听、刷新数据、恢复过滤。</summary>
    public void Initialize()
    {
        DirText = "数据目录：" + AppRuntime.Paths.DataDir;

        var settings = AppRuntime.Settings.Value;
        FilterText = settings.LastFilter;
        SelectedGroup = settings.LastGroup;

        RefreshData();
        StartWatcher();
    }

    // ---------------------------------------------------------------- 数据刷新

    private void StartWatcher()
    {
        try
        {
            _watcher = new RegistryWatcher();
            _watcher.Changed += () =>
            {
                // 注册表监听在后台线程触发，切回 UI 线程刷新
                AppRuntime.UiQueue?.TryEnqueue(RefreshData);
            };
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"启动注册表监听失败，改为手动刷新：{ex.Message}");
        }
    }

    public void RefreshData()
    {
        if (_refreshing) return;
        _refreshing = true;

        try
        {
            var selectedKey = SelectedRow?.Key;
            var entries = _service.Snapshot(out var readError);

            _rows = entries.Select(e => new EntryRowViewModel(e, AppRuntime.Meta.Get(e.Key))).ToList();

            RebuildGroupFilter();
            ApplyFilter(selectedKey);

            CountText = $"共 {_rows.Count} 条";
            if (readError is not null)
            {
                CountText += "（读取注册表失败）";
                LogService.Current?.Error($"刷新时读取注册表失败：{readError}");
            }

            _ipState = IpHelperService.GetState();
            _deadMappings = FindMappingsNotListening(entries);
            UpdateHelperStatus();
            UpdateBanner();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RebuildGroupFilter()
    {
        var groups = _rows.Select(r => r.Group)
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(g => g, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        GroupOptions.Clear();
        GroupOptions.Add(AllGroupsLabel);
        foreach (var group in groups) GroupOptions.Add(group);

        var current = SelectedGroup;
        SelectedGroup = !string.IsNullOrWhiteSpace(current) && groups.Contains(current, StringComparer.CurrentCultureIgnoreCase)
            ? current
            : AllGroupsLabel;

        // 集合重建会把 ComboBox 的选中清掉；若字段值恰好没变，
        // SelectedGroup 赋值不会触发通知，这里强制刷新一次显示
        OnPropertyChanged(nameof(SelectedGroup));
    }

    private void ApplyFilter(string? restoreSelectionKey = null)
    {
        restoreSelectionKey ??= SelectedRow?.Key;

        var filter = FilterText.Trim();
        var group = SelectedGroup;

        IEnumerable<EntryRowViewModel> query = _rows;

        if (group.Length > 0 && group != AllGroupsLabel)
            query = query.Where(r => string.Equals(r.Group, group, StringComparison.CurrentCultureIgnoreCase));

        if (filter.Length > 0)
            query = query.Where(r => r.FullText.Contains(filter, StringComparison.CurrentCultureIgnoreCase));

        var list = query.ToList();

        if (_sortProperty is not null)
            list.Sort((a, b) => CompareRows(a, b, _sortProperty, _sortAscending));

        View.Clear();
        foreach (var row in list) View.Add(row);

        // 恢复刷新前的选中行
        if (restoreSelectionKey is not null)
            SelectedRow = View.FirstOrDefault(r =>
                string.Equals(r.Key, restoreSelectionKey, StringComparison.OrdinalIgnoreCase))
                ?? View.FirstOrDefault();

        PersistFilterPrefs();
    }

    /// <summary>过滤/分组变化时记入设置，与旧版行为一致。</summary>
    private void PersistFilterPrefs()
    {
        var settings = AppRuntime.Settings.Value;
        if (settings.LastFilter != FilterText || settings.LastGroup != SelectedGroup)
        {
            settings.LastFilter = FilterText;
            settings.LastGroup = SelectedGroup;
            AppRuntime.Settings.Save();
        }
    }

    /// <summary>DataGrid 表头排序入口（Sorting 事件）。</summary>
    public void SortBy(string property)
    {
        if (string.Equals(_sortProperty, property, StringComparison.Ordinal))
            _sortAscending = !_sortAscending;
        else
        {
            _sortProperty = property;
            _sortAscending = true;
        }

        ApplyFilter();
    }

    public bool IsSortAscending(string property) =>
        string.Equals(_sortProperty, property, StringComparison.Ordinal) && _sortAscending;

    public string? SortProperty => _sortProperty;

    private static int CompareRows(EntryRowViewModel a, EntryRowViewModel b, string property, bool ascending)
    {
        var result = property switch
        {
            nameof(EntryRowViewModel.Name) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            nameof(EntryRowViewModel.Group) => string.Compare(a.Group, b.Group, StringComparison.CurrentCultureIgnoreCase),
            nameof(EntryRowViewModel.Type) => string.Compare(a.Type, b.Type, StringComparison.OrdinalIgnoreCase),
            nameof(EntryRowViewModel.ListenAddress) => CompareAddress(a.ListenAddress, b.ListenAddress),
            nameof(EntryRowViewModel.ListenPort) => a.ListenPort.CompareTo(b.ListenPort),
            nameof(EntryRowViewModel.ConnectAddress) => CompareAddress(a.ConnectAddress, b.ConnectAddress),
            nameof(EntryRowViewModel.ConnectPort) => a.ConnectPort.CompareTo(b.ConnectPort),
            nameof(EntryRowViewModel.Follow) => string.Compare(a.Follow, b.Follow, StringComparison.CurrentCultureIgnoreCase),
            nameof(EntryRowViewModel.Firewall) => string.Compare(a.Firewall, b.Firewall, StringComparison.Ordinal),
            nameof(EntryRowViewModel.Note) => string.Compare(a.Note, b.Note, StringComparison.CurrentCultureIgnoreCase),
            _ => 0,
        };

        return ascending ? result : -result;
    }

    private static int CompareAddress(string a, string b)
    {
        // *（所有地址）排最前，其余按字符串比较
        if (a == "*" && b != "*") return -1;
        if (b == "*" && a != "*") return 1;
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateHelperStatus()
    {
        var state = _ipState;
        HelperText = state switch
        {
            IpHlpState.Running => "IP Helper：运行中",
            IpHlpState.Stopped => "IP Helper：已停止",
            IpHlpState.NotFound => "IP Helper：未安装",
            _ => "IP Helper：状态未知",
        };
        HelperIsRunning = state == IpHlpState.Running;
    }

    // ---------------------------------------------------------------- 横幅

    private void UpdateBanner()
    {
        var state = _ipState;
        var lines = new List<string>();
        Action? action = null;
        string actionText = string.Empty;

        if (state == IpHlpState.Stopped)
        {
            lines.Add("IP Helper（iphlpsvc）没有运行 —— 新增的映射不会生效。");
            if (AppRuntime.Elevated)
            {
                action = StartHelperService;
                actionText = "启动服务";
            }
        }
        else if (state == IpHlpState.NotFound)
        {
            lines.Add("系统里找不到 IP Helper（iphlpsvc）服务，端口转发功能不可用。");
        }
        else if (_deadMappings.Count > 0)
        {
            var ports = string.Join("、", _deadMappings.Take(4).Select(e => e.ListenPort));
            var legacy = _deadMappings.FirstOrDefault(IsLegacyWildcard);

            var reserved = _deadMappings
                .Select(e => (Entry: e, Range: PortReservationService.FindContaining(e.ListenPort)))
                .FirstOrDefault(t => t.Range is not null);

            if (reserved.Entry is not null && reserved.Range is not null)
            {
                var range = reserved.Range.Value;
                lines.Add($"端口 {reserved.Entry.ListenPort} 落在系统保留区间 {range.Start}–{range.End}" +
                          "（由 Hyper-V / WSL / WinNAT 占用），任何程序都无法监听它 —— " +
                          "请改用其他端口；这些区间每次开机可能变化，所以以前能用的端口也会突然失效。");
            }
            else if (legacy is not null)
            {
                lines.Add($"端口 {legacy.ListenPort} 没有在监听：这条映射的监听地址在注册表里存成了 *，" +
                          "不是 netsh 的标准写法（0.0.0.0），IP Helper 通常无法加载它。");
                if (AppRuntime.Elevated)
                {
                    action = () => RebuildMapping(legacy);
                    actionText = "重建为 0.0.0.0";
                }
            }
            else
            {
                lines.Add($"有 {_deadMappings.Count} 条映射没有生效：端口 {ports} 没有在监听 —— " +
                          "配置存在但 IP Helper 没有加载它。");
                if (AppRuntime.Elevated)
                {
                    action = RestartHelperService;
                    actionText = "重启 IP Helper";
                }
            }
        }

        if (AppRuntime.Paths.FallbackReason is not null)
        {
            lines.Add($"数据目录已降级到 {AppRuntime.Paths.DataDir}（{AppRuntime.Paths.FallbackReason}）。");
            if (action is null)
            {
                action = OpenDataDir;
                actionText = "打开目录";
            }
        }

        _bannerAction = action;
        BannerMessage = string.Join("\n", lines);
        BannerActionText = actionText;
        BannerVisible = lines.Count > 0;
    }

    [RelayCommand]
    private void RunBannerAction() => _bannerAction?.Invoke();

    /// <summary>
    /// 找出"配置存在但端口没有被监听"的映射。
    /// portproxy 的监听由 IP Helper 负责，服务没加载配置时端口就是空的。
    /// </summary>
    private IReadOnlyList<PortProxyEntry> FindMappingsNotListening(IReadOnlyList<PortProxyEntry> entries)
    {
        if (entries.Count == 0) return Array.Empty<PortProxyEntry>();

        try
        {
            var listeningPorts = new HashSet<int>(
                RegistryReader.ActiveTcpListeners().Select(l => l.Port));

            return entries.Where(e => !listeningPorts.Contains(e.ListenPort)).ToList();
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"检查映射监听状态失败：{ex.Message}");
            return Array.Empty<PortProxyEntry>();
        }
    }

    /// <summary>注册表里把通配监听地址存成了字面量 *（非 netsh 标准形式）。</summary>
    private static bool IsLegacyWildcard(PortProxyEntry entry) =>
        entry.ListenAddress.Trim() == "*";

    /// <summary>
    /// 用标准写法重建一条映射：删除旧的 * 条目，再以 0.0.0.0（或 ::）添加。
    /// 删除时优先使用原始地址，因为 netsh 需要精确匹配注册表里的键名。
    /// </summary>
    private async void RebuildMapping(PortProxyEntry entry)
    {
        if (!await RequireElevationAsync()) return;

        var nl = Environment.NewLine;
        var confirmed = await Dialogs.ConfirmAsync(
            "重建映射",
            "将把这条映射重建为标准写法：" + nl + nl +
            $"原：{entry.Family} {entry.ListenText} → {entry.ConnectText}（监听地址存为 *）" + nl +
            $"新：{entry.Family} {entry.NetshListenAddress}:{entry.ListenPort} → {entry.ConnectText}" + nl + nl +
            "期间该端口会短暂中断，当前配置会先自动备份。继续吗？");
        if (!confirmed) return;

        BackupService.WriteSnapshot(
            AppRuntime.Paths, _service.Snapshot(), AppRuntime.Meta, "pre-rebuild", AppRuntime.Settings.Value);

        await RunGuardedAsync("重建映射", () =>
        {
            _service.Delete(entry, useRawListenAddress: true);

            var rebuilt = new PortProxyEntry
            {
                Family = entry.Family,
                Protocol = entry.Protocol,
                ListenAddress = entry.NetshListenAddress,
                ListenPort = entry.ListenPort,
                ConnectAddress = entry.NormalizedConnect,
                ConnectPort = entry.ConnectPort,
            };

            _service.Add(rebuilt);
        });

        ScheduleHealthRecheck();
    }

    /// <summary>写操作后监听器需要一点时间才出现，稍后再复查一次避免误报。</summary>
    private void ScheduleHealthRecheck()
    {
        var queue = AppRuntime.UiQueue;
        if (queue is null) return;

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(ProbeTimeouts.StatusTimerMs);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => timer.Stop();
        timer.Tick += (_, _) => RefreshData();
        timer.Start();
    }

    // ---------------------------------------------------------------- 服务控制

    [RelayCommand]
    private void StartHelperService()
    {
        var (ok, message) = IpHelperService.TryStart();
        RefreshData();
        _ = Dialogs.InfoAsync("IP Helper", message);
    }

    [RelayCommand]
    private void RestartHelperService()
    {
        var (ok, message) = IpHelperService.TryRestart();
        RefreshData();
        _ = Dialogs.InfoAsync("IP Helper", message);
    }

    [RelayCommand]
    private void OpenDataDir()
    {
        try
        {
            Directory.CreateDirectory(AppRuntime.Paths.DataDir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppRuntime.Paths.DataDir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _ = Dialogs.InfoAsync("数据目录", $"打开目录失败：{ex.Message}");
        }
    }

    // ---------------------------------------------------------------- 增删改

    /// <summary>工具栏/右键菜单命令：作用于当前选中行。</summary>
    [RelayCommand]
    private Task EditSelected() => EditEntryAsync(null);

    /// <summary>工具栏/右键菜单命令：删除当前选中行。</summary>
    [RelayCommand]
    private Task DeleteSelected() => DeleteEntryAsync(null);

    [RelayCommand]
    private async Task AddEntryAsync()
    {
        if (!await RequireElevationAsync()) return;

        var (ok, entry, meta, firewall) = await EntryEditWindow.ShowAsync(
            existing: null, meta: null, _rows.Select(r => r.Entry).ToList());
        if (!ok || entry is null) return;

        await RunGuardedAsync("新增映射", () =>
        {
            var added = _service.Add(entry);
            if (meta is not null && !meta.IsEmpty)
            {
                meta.CreatedAt ??= DateTimeOffset.Now;
                AppRuntime.Meta.Set(added.Key, meta);
                AppRuntime.Meta.Save();
            }

            if (firewall)
            {
                var (ruleOk, message) = FirewallService.Add(added);
                if (!ruleOk) throw new PortProxyException($"映射已创建，但防火墙规则添加失败：{message}");

                var stored = AppRuntime.Meta.GetOrCreate(added.Key);
                stored.HasFirewallRule = true;
                AppRuntime.Meta.Save();
            }
        });
    }

    public async Task EditEntryAsync(EntryRowViewModel? row = null)
    {
        row ??= SelectedRow;
        if (row is null) return;
        if (!await RequireElevationAsync()) return;

        var (ok, updated, meta, _) = await EntryEditWindow.ShowAsync(
            existing: row.Entry, meta: row.Meta, _rows.Select(r => r.Entry).ToList());
        if (!ok || updated is null) return;

        if (row.Entry.SameTarget(updated))
        {
            // 只改了备注
            SaveMetaOnly(row.Entry.Key, meta);
            RefreshData();
            return;
        }

        BackupService.WriteSnapshot(
            AppRuntime.Paths, _service.Snapshot(), AppRuntime.Meta, "pre-update", AppRuntime.Settings.Value);

        await RunGuardedAsync("修改映射", () =>
        {
            _service.Update(row.Entry, updated);

            if (!string.Equals(row.Entry.Key, updated.Key, StringComparison.OrdinalIgnoreCase))
                AppRuntime.Meta.Remove(row.Entry.Key);

            if (meta is not null && !meta.IsEmpty)
                AppRuntime.Meta.Set(updated.Key, meta);

            AppRuntime.Meta.Save();

            if (row.Meta?.HasFirewallRule == true)
            {
                var (fwOk, message) = FirewallService.Delete(row.Entry);
                LogService.Current?.Info($"编辑后清理旧防火墙规则：{message}（{(fwOk ? "成功" : "失败")}）");
            }
        });
    }

    private async Task DeleteEntryAsync(EntryRowViewModel? row = null)
    {
        row ??= SelectedRow;
        if (row is null) return;
        if (!await RequireElevationAsync()) return;

        var name = string.IsNullOrWhiteSpace(row.Name) ? "（无名称）" : row.Name;
        var confirmed = await Dialogs.ConfirmAsync(
            "确认删除",
            "确定要删除这条映射吗？\n\n" +
            $"名称：{name}\n" +
            $"类型：{row.Entry.Family}\n" +
            $"监听：{row.Entry.ListenText}\n" +
            $"目标：{row.Entry.ConnectText}\n\n" +
            "删除前会把当前全部配置自动备份到 backups 目录。",
            defaultIsYes: false);
        if (!confirmed) return;

        BackupService.WriteSnapshot(
            AppRuntime.Paths, _service.Snapshot(), AppRuntime.Meta, "pre-delete", AppRuntime.Settings.Value);

        var hadFirewall = row.Meta?.HasFirewallRule == true;
        var deleted = await RunGuardedAsync("删除映射", () =>
        {
            _service.Delete(row.Entry);
            AppRuntime.Meta.Remove(row.Entry.Key);
            AppRuntime.Meta.Save();
        });

        if (!deleted || !hadFirewall) return;

        if (await Dialogs.ConfirmAsync("清理防火墙",
                $"是否同时删除这条映射的防火墙放行规则？（TCP {row.Entry.ListenPort}）"))
        {
            var (ok, message) = FirewallService.Delete(row.Entry);
            if (!ok) _ = Dialogs.InfoAsync("防火墙", message);
            else RefreshData();
        }
    }

    private static void SaveMetaOnly(string key, EntryMeta? meta)
    {
        if (meta is null || meta.IsEmpty) AppRuntime.Meta.Remove(key);
        else AppRuntime.Meta.Set(key, meta);
        AppRuntime.Meta.Save();
    }

    /// <summary>统一的执行包装：异常翻译、完成后刷新。返回动作是否成功。</summary>
    private async Task<bool> RunGuardedAsync(string title, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (PortProxyException ex)
        {
            LogService.Current?.Error($"{title}失败：{ex.Message}");
            await Dialogs.InfoAsync(title, $"{title}失败：\n\n{ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            LogService.Current?.Error($"{title}异常：{ex}");
            await Dialogs.InfoAsync(title, $"{title}时发生意外错误：\n\n{ex.Message}");
            return false;
        }
        finally
        {
            RefreshData();
        }
    }

    /// <summary>未提权时询问是否以管理员身份重启。</summary>
    private async Task<bool> RequireElevationAsync()
    {
        if (AppRuntime.Elevated) return true;

        var restart = await Dialogs.ConfirmAsync(
            "需要管理员权限",
            "修改端口映射需要管理员权限。\n\n是否以管理员身份重新启动本程序？");
        if (!restart) return false;

        var args = Environment.GetCommandLineArgs().Skip(1)
            .Append("--data-dir=" + AppRuntime.Paths.DataDir)
            .ToArray();
        var code = Cli.RelaunchElevated(args, wait: false);

        if (code != 0)
            _ = Dialogs.InfoAsync("提示", "提权启动失败，或用户取消了 UAC。");
        else
            AppRuntime.MainWindow?.Close();

        return false;
    }

    // ---------------------------------------------------------------- 行操作

    [RelayCommand]
    private void CopySelectedNetsh() => CopyNetshCommand(null);

    private void CopyNetshCommand(EntryRowViewModel? row = null)
    {
        row ??= SelectedRow;
        if (row is null) return;

        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(row.Entry.NetshPreview);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"复制到剪贴板失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private Task ProbeSelected() => ProbeTargetAsync(null);

    private async Task ProbeTargetAsync(EntryRowViewModel? row = null)
    {
        row ??= SelectedRow;
        if (row is null) return;

        try
        {
            var (ok, detail) = await PortProbe.TestConnectAsync(row.Entry.NormalizedConnect, row.Entry.ConnectPort);
            await Dialogs.InfoAsync(ok ? "连接测试：成功" : "连接测试：失败", detail);
        }
        catch (Exception ex)
        {
            LogService.Current?.Error($"连通性测试异常：{ex}");
            await Dialogs.InfoAsync("连接测试", $"测试失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _watcher = null;
    }
}
