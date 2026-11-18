using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using PortProxyManager.Models;
using PortProxyManager.Services;
using PortProxyManager.WinUI.Views;

namespace PortProxyManager.WinUI.ViewModels;

/// <summary>
/// 目标候选下拉里的一项：探测到的地址（可跟随与否由 Hint 决定）、
/// 编辑离线跟随条目时的兜底项，或"手动填写 IP"入口。
/// 对齐 v2.0.0 EntryDialog 的 CandidateItem。
/// </summary>
public sealed class CandidateRow
{
    public CandidateRow(AddressHint? hint, FollowTarget? fallbackTarget, string display)
    {
        Hint = hint;
        FallbackTarget = fallbackTarget;
        Display = display;
    }

    public AddressHint? Hint { get; }
    public FollowTarget? FallbackTarget { get; }
    public string Display { get; }

    public bool IsManualEntry => Hint is null && FallbackTarget is null;

    public bool IsFollowable =>
        (Hint is not null && TargetFollowService.IsFollowable(Hint))
        || FallbackTarget is not null;

    public override string ToString() => Display;
}

/// <summary>
/// 新增/编辑映射表单。对齐 v2.0.0 EntryDialog：
/// 单一"目标候选"可编辑下拉（手输 / 探测地址 / 离线跟随兜底）+
/// "跟随"开关（仅可跟随设备可用），跟随与连通性测试并排。
/// </summary>
public partial class EntryEditViewModel : ObservableObject
{
    public const string AutoFamilyLabel = "自动（按地址推断）";

    private readonly PortProxyEntry? _existing;
    private readonly EntryMeta? _existingMeta;
    private readonly IReadOnlyList<PortProxyEntry> _allEntries;

    private FollowTarget? _initialFollowTarget;
    private bool _followStoppedByUser;
    private bool _populatingCandidates;
    private int _populateVersion;

    /// <summary>确认后由窗口关闭自身。</summary>
    public event Action? CloseRequested;

    /// <summary>确认按钮是否成功产出结果（用户点关闭/取消时为 false）。</summary>
    public bool Confirmed { get; private set; }

    public PortProxyEntry? Result { get; private set; }
    public EntryMeta? ResultMeta { get; private set; }

    public ObservableCollection<string> FamilyOptions { get; } = new()
    {
        AutoFamilyLabel, "v4tov4", "v4tov6", "v6tov4", "v6tov6",
    };

    /// <summary>目标候选下拉项（加载/下拉展开时由 PopulateCandidates 填充）。</summary>
    public ObservableCollection<CandidateRow> CandidateOptions { get; } = new();

    public string Title => _existing is null ? "新增端口映射" : "编辑端口映射";
    public string ConfirmText => _existing is null ? "创建" : "保存";

    [ObservableProperty]
    public partial string SelectedFamily { get; set; } = AutoFamilyLabel;

    [ObservableProperty]
    public partial string ListenAddress { get; set; } = "0.0.0.0";

    [ObservableProperty]
    public partial double ListenPort { get; set; } = 8080;

    [ObservableProperty]
    public partial double ConnectPort { get; set; } = 80;

    /// <summary>当前选中的候选；null = 手输状态。</summary>
    [ObservableProperty]
    public partial CandidateRow? SelectedCandidate { get; set; }

    /// <summary>手输/候选展示文本（与输入框双向同步，取值时经清洗）。</summary>
    [ObservableProperty]
    public partial string ConnectText { get; set; } = "127.0.0.1";

    /// <summary>跟随开关：只对可跟随的候选（WSL / 虚拟机 / 沙盒）可点。</summary>
    [ObservableProperty]
    public partial bool FollowChecked { get; set; }

    /// <summary>跟随开关可用性：由当前选中候选决定。</summary>
    public bool FollowEnabled => SelectedCandidate?.IsFollowable == true;

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Group { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Note { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CreateFirewallRule { get; set; }

    [ObservableProperty]
    public partial string PreviewText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    /// <summary>错误提示：文本为空时隐藏。</summary>
    public Visibility ErrorVisibility =>
        ErrorText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    public partial string TestResultText { get; set; } = "测试目标端口是否可达";

    public EntryEditViewModel(
        PortProxyEntry? existing,
        EntryMeta? meta,
        IReadOnlyList<PortProxyEntry> allEntries)
    {
        _existing = existing;
        _existingMeta = meta;
        _allEntries = allEntries;
    }

    // 输入变化 → 实时刷新等价命令预览 / 跟随开关状态
    partial void OnSelectedFamilyChanged(string value) => UpdatePreview();
    partial void OnListenAddressChanged(string value) => UpdatePreview();
    partial void OnListenPortChanged(double value) => UpdatePreview();
    partial void OnConnectPortChanged(double value) => UpdatePreview();
    partial void OnConnectTextChanged(string value) => UpdatePreview();

    partial void OnSelectedCandidateChanged(CandidateRow? value)
    {
        if (_populatingCandidates) return;

        UpdateFollowToggle();
        UpdatePreview();
    }

    partial void OnFollowCheckedChanged(bool value) => UpdatePreview();

    /// <summary>跟随开关只对可跟随的设备（WSL / 虚拟机 / 沙盒）可点击。</summary>
    private void UpdateFollowToggle()
    {
        if (!FollowEnabled && FollowChecked)
            FollowChecked = false;
        OnPropertyChanged(nameof(FollowEnabled));
    }

    /// <summary>
    /// 重建目标候选下拉：第一项为"手动填写 IP"入口，其后是网关 / 局域网设备 /
    /// 已确认运行中的 WSL / 虚拟机 / 沙盒。编辑离线跟随条目时补一个兜底项。
    /// </summary>
    public void PopulateCandidates()
    {
        if (_populatingCandidates) return;

        _populatingCandidates = true;
        try
        {
            _populateVersion++;

            // 启动时已预热；这里只读缓存，避免展开下拉时被 arp.exe / wsl.exe 阻塞。
            var snapshot = GuestHintCache.Snapshot;
            var source = snapshot.Count > 0
                ? snapshot
                : NetworkHintService.FilterUnconfirmedGuests(NetworkHintService.GetHints());

            var sandboxMacs = new HashSet<string>(
                source
                    .Where(h => h.Category.Contains("沙盒", StringComparison.Ordinal) && h.Mac is not null)
                    .Select(h => h.Mac!),
                StringComparer.OrdinalIgnoreCase);

            var merged = new List<AddressHint>();
            var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenSandboxMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var hint in source)
            {
                // 沙盒同一 MAC 可能出现在多个虚拟交换机上；只保留优先级最高的一条
                if (hint.Mac is not null
                    && sandboxMacs.Contains(hint.Mac)
                    && !seenSandboxMacs.Add(hint.Mac))
                    continue;

                if (seenAddresses.Add(hint.Address)) merged.Add(hint);
            }

            var items = new List<CandidateRow> { new(null, null, "— 手动填写 IP —") };

            foreach (var hint in merged)
            {
                var followable = TargetFollowService.IsFollowable(hint);
                items.Add(new CandidateRow(
                    hint,
                    followable ? FollowTarget.FromHint(hint) : null,
                    hint.Display));
            }

            // 正在跟随的条目：优先选中对应候选；目标未检测到时补一个兜底项
            var previousTarget = _followStoppedByUser ? null : _initialFollowTarget;
            if (previousTarget is not null)
            {
                var matched = items.FirstOrDefault(i =>
                    i.Hint is not null && TargetFollowService.Matches(i.Hint, previousTarget));

                if (matched is null)
                {
                    items.Add(new CandidateRow(
                        null, previousTarget,
                        $"{previousTarget.Display}（当前未检测到，保留原地址）"));
                }
            }

            CandidateOptions.Clear();
            foreach (var item in items) CandidateOptions.Add(item);

            if (previousTarget is not null)
            {
                SelectedCandidate =
                    items.FirstOrDefault(i =>
                        i.Hint is not null && TargetFollowService.Matches(i.Hint, previousTarget))
                    ?? items.FirstOrDefault(i => i.FallbackTarget is not null
                        && TargetFollowService.TargetEquals(i.FallbackTarget, previousTarget))
                    ?? items[0];
                FollowChecked = true;
                ConnectText = GetCurrentConnectAddress();
            }
            else
            {
                SelectedCandidate = null;
                FollowChecked = false;

                var current = TryExtractAddress(ConnectText) ?? ConnectText.Trim();
                if (current.Length > 0)
                    ConnectText = current;
            }
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"填充目标候选失败：{ex.Message}");
        }
        finally
        {
            _populatingCandidates = false;
        }

        UpdateFollowToggle();

        // 重建候选集合时 ComboBox 内部文本会被清空（OneWay 绑定不会自动回写），
        // 主动通知一次，让目标框重新显示当前地址/手输值。
        OnPropertyChanged(nameof(ConnectText));

        UpdatePreview();
    }

    /// <summary>
    /// 当前生效的目标地址：选中候选取其探测地址（离线跟随保留原地址），
    /// 手输状态取输入框文本。
    /// </summary>
    public string GetCurrentConnectAddress()
    {
        if (SelectedCandidate is { } item)
        {
            if (item.Hint?.Address is { Length: > 0 } hintAddress) return hintAddress;
            return _existing?.NormalizedConnect ?? string.Empty;
        }

        return TryExtractAddress(ConnectText) ?? ConnectText.Trim();
    }

    /// <summary>用户手输提交（Enter / 失焦）：手动填写项之外的文本保留原样。</summary>
    public void SubmitConnectText(string text)
    {
        if (_populatingCandidates) return;

        // 手输状态：用户放弃跟随
        _followStoppedByUser = true;
        SelectedCandidate = null;
        ConnectText = TryExtractAddress(text) ?? text.Trim();

        UpdateFollowToggle();
        UpdatePreview();
    }

    /// <summary>选中下拉候选：手动填写项清空文本，其余保留展示文本。</summary>
    public void CandidateCommitted(CandidateRow? item)
    {
        if (_populatingCandidates) return;

        if (item is not null)
        {
            if (item.IsManualEntry)
                ConnectText = string.Empty;
            _followStoppedByUser = false;
        }

        UpdateFollowToggle();
        UpdatePreview();
    }

    /// <summary>
    /// 窗口显示后等待一次候选刷新（如果启动预热还在跑就复用它），
    /// 完成后补填下拉。
    /// </summary>
    public async Task RefreshCandidatesWhenReadyAsync()
    {
        try
        {
            await GuestHintCache.RefreshNowAsync(force: false);

            // 服务层的续体可能不在 UI 线程；重建候选会触碰绑定集合，必须切回
            var queue = AppRuntime.UiQueue;
            if (queue is not null && !queue.HasThreadAccess)
                queue.TryEnqueue(PopulateCandidates);
            else
                PopulateCandidates();
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"补充目标候选失败：{ex.Message}");
        }
    }

    /// <summary>从展示文本里取回纯 IP（支持新格式 "IP - type:id" 与旧格式 "IP · 说明"）。</summary>
    private static string? TryExtractAddress(string text)
    {
        var candidate = text.Trim();

        var legacySeparator = candidate.IndexOf("  ·  ", StringComparison.Ordinal);
        if (legacySeparator > 0) candidate = candidate[..legacySeparator].Trim();

        var separator = candidate.IndexOf(" - ", StringComparison.Ordinal);
        if (separator > 0) candidate = candidate[..separator].Trim();

        return System.Net.IPAddress.TryParse(candidate, out var ip)
               && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? candidate
            : null;
    }

    /// <summary>编辑模式时载入现有值。</summary>
    public void LoadValues()
    {
        if (_existing is null) return;

        SelectedFamily = _existing.Family;
        ListenAddress = _existing.NormalizedListen;
        ListenPort = Math.Clamp(_existing.ListenPort, 1, 65535);
        ConnectText = _existing.NormalizedConnect;
        ConnectPort = Math.Clamp(_existing.ConnectPort, 1, 65535);

        if (_existingMeta is null) return;

        Name = _existingMeta.Name ?? string.Empty;
        Group = _existingMeta.Group ?? string.Empty;
        Note = _existingMeta.Note ?? string.Empty;
        CreateFirewallRule = _existingMeta.HasFirewallRule;
        _initialFollowTarget = FollowTarget.FromMeta(_existingMeta);
    }

    private string ResolveFamily() =>
        SelectedFamily == AutoFamilyLabel
            ? Validator.InferFamily(ListenAddress, GetCurrentConnectAddress())
            : SelectedFamily;

    private PortProxyEntry? BuildEntry(string family, out string error)
    {
        error = string.Empty;

        if (!Validator.TryNormalizeAddress(
                ListenAddress, !family.StartsWith("v4"), out var listen, out var listenError))
        {
            error = listenError;
            return null;
        }

        var connectText = GetCurrentConnectAddress();

        if (!Validator.TryNormalizeAddress(
                connectText, family.EndsWith("v6"), out var connect, out var connectError))
        {
            error = connectError;
            return null;
        }

        return new PortProxyEntry
        {
            Family = family,
            Protocol = "tcp",
            ListenAddress = listen,
            ListenPort = (int)Math.Clamp(ListenPort, 1, 65535),
            ConnectAddress = connect,
            ConnectPort = (int)Math.Clamp(ConnectPort, 1, 65535),
        };
    }

    private void UpdatePreview()
    {
        var family = ResolveFamily();
        var entry = BuildEntry(family, out _);

        PreviewText = entry is null
            ? "（地址或端口不合法，无法生成命令）"
            : entry.NetshPreview;
    }

    /// <summary>窗口就绪后主动刷一次预览（新增模式的初始值）。</summary>
    public void RefreshPreview() => UpdatePreview();

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke();

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        ErrorText = string.Empty;

        if (FollowChecked && GetCurrentConnectAddress().Length == 0)
        {
            ErrorText = "跟随目标当前未检测到，且没有已保存的目标地址。";
            return;
        }

        var family = ResolveFamily();
        var entry = BuildEntry(family, out var buildError);

        if (entry is null)
        {
            ErrorText = buildError;
            return;
        }

        var validation = Validator.Validate(entry, _allEntries, _existing?.Key);

        if (!validation.Ok)
        {
            ErrorText = validation.Error ?? "校验未通过。";
            return;
        }

        if (!string.IsNullOrWhiteSpace(validation.Warning))
        {
            var go = await Dialogs.ConfirmAsync(
                "端口提示", validation.Warning + "\n\n仍要继续吗？", defaultIsYes: false);
            if (!go) return;
        }

        if (_existing is not null &&
            !string.Equals(_existing.Key, entry.Key, StringComparison.OrdinalIgnoreCase))
        {
            var go = await Dialogs.ConfirmAsync(
                "确认修改",
                "监听地址或端口发生了变化：\n\n" +
                $"原：{_existing.Family} {_existing.ListenText}\n" +
                $"新：{entry.Family} {entry.ListenText}\n\n" +
                "portproxy 不支持原地修改，会先删除旧映射再创建新的。继续吗？");
            if (!go) return;
        }

        if (CreateFirewallRule && !AppRuntime.Elevated)
        {
            ErrorText = "创建防火墙规则需要管理员权限。";
            return;
        }

        // 跟随：开关打开且选中候选 → 取候选对应的 FollowTarget
        var selectedItem = SelectedCandidate;
        var follow = FollowChecked && selectedItem is not null
            ? selectedItem.Hint is not null
                ? FollowTarget.FromHint(selectedItem.Hint)
                : selectedItem.FallbackTarget
            : null;

        Result = entry;
        ResultMeta = new EntryMeta
        {
            Name = Trimmed(Name),
            Group = Trimmed(Group),
            Note = Trimmed(Note),
            HasFirewallRule = CreateFirewallRule,
            CreatedAt = _existingMeta?.CreatedAt ?? DateTimeOffset.Now,
            FollowKind = follow?.Kind,
            FollowId = follow?.Id,
            FollowMac = follow?.Mac,
            FollowLabel = follow?.Label,
        };

        Confirmed = true;

        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private async Task TestConnectAsync()
    {
        TestResultText = "正在测试…";
        var (ok, detail) = await PortProbe.TestConnectAsync(
            GetCurrentConnectAddress(), (int)Math.Clamp(ConnectPort, 1, 65535));

        // PortProbe 用 ConfigureAwait(false)，续体可能在线程池上；
        // 绑定属性必须切回 UI 线程更新。
        SetTestResult(detail);
    }

    private void SetTestResult(string text)
    {
        var queue = AppRuntime.UiQueue;
        if (queue is not null && !queue.HasThreadAccess)
        {
            queue.TryEnqueue(() => TestResultText = text);
            return;
        }

        TestResultText = text;
    }

    private static string? Trimmed(string value)
    {
        var text = value.Trim();
        return text.Length == 0 ? null : text;
    }
}
