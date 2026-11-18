using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using PortProxyManager.Models;
using PortProxyManager.Services;
using PortProxyManager.WinUI.Views;

namespace PortProxyManager.WinUI.ViewModels;

/// <summary>目标地址候选（下拉项）：展示文本 + 选中后回填的纯 IP。</summary>
public sealed class AddressOptionRow
{
    public AddressOptionRow(AddressHint hint)
    {
        Hint = hint;
        Display = hint.Display;
        Address = hint.Address;
    }

    public AddressHint Hint { get; }
    public string Display { get; }
    public string Address { get; }
}

/// <summary>目标跟随选择（"不跟随" + 各可跟随目标）。</summary>
public sealed class FollowChoiceRow
{
    public FollowChoiceRow(FollowTarget? target, AddressHint? hint)
    {
        Target = target;
        Hint = hint;
        Display = target is null
            ? "不跟随（固定 IP）"
            : $"{target.Display} → {hint?.Address ?? "（当前未检测到）"}";
    }

    public FollowTarget? Target { get; }
    public AddressHint? Hint { get; }
    public string Display { get; }
}

/// <summary>
/// 新增/编辑映射表单。旧版 EntryDialog 的表单逻辑：
/// family 推断、地址归一化校验、实时等价命令预览、完整 Validator 校验、
/// 目标地址候选下拉（GuestHintCache）与目标跟随选择器。
/// </summary>
public partial class EntryEditViewModel : ObservableObject
{
    public const string AutoFamilyLabel = "自动（按地址推断）";

    private readonly PortProxyEntry? _existing;
    private readonly EntryMeta? _existingMeta;
    private readonly IReadOnlyList<PortProxyEntry> _allEntries;

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

    /// <summary>目标地址候选（打开下拉时由 GuestHintCache 快照填充）。</summary>
    public ObservableCollection<AddressOptionRow> AddressOptions { get; } = new();

    /// <summary>跟随选择项：首项固定"不跟随"。</summary>
    public ObservableCollection<FollowChoiceRow> FollowOptions { get; } = new();

    private FollowTarget? _initialFollowTarget;
    private bool _populatingFollow;

    public string Title => _existing is null ? "新增端口映射" : "编辑端口映射";
    public string ConfirmText => _existing is null ? "创建" : "保存";

    [ObservableProperty]
    public partial string SelectedFamily { get; set; } = AutoFamilyLabel;

    [ObservableProperty]
    public partial string ListenAddress { get; set; } = "0.0.0.0";

    [ObservableProperty]
    public partial double ListenPort { get; set; } = 8080;

    [ObservableProperty]
    public partial string ConnectAddress { get; set; } = "127.0.0.1";

    [ObservableProperty]
    public partial double ConnectPort { get; set; } = 80;

    /// <summary>当前选中的跟随项；null = 不跟随。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectEnabled))]
    public partial FollowChoiceRow? SelectedFollow { get; set; }

    /// <summary>跟随状态下目标地址由探测结果决定，禁止手改（与旧版一致）。</summary>
    public bool ConnectEnabled => SelectedFollow?.Target is null;

    /// <summary>选择跟随项：禁用手输并回填探测到的地址。</summary>
    partial void OnSelectedFollowChanged(FollowChoiceRow? value)
    {
        if (_populatingFollow) return;

        if (value?.Target is not null && !string.IsNullOrEmpty(value.Hint?.Address))
            ConnectAddress = value.Hint!.Address;

        UpdatePreview();
    }

    /// <summary>
    /// 重建跟随选择项（读 GuestHintCache 快照，不阻塞）。
    /// 已保存但当前探测不到的目标保留一项，避免编辑离线虚拟机时丢配置。
    /// </summary>
    public void PopulateFollowChoices()
    {
        if (_populatingFollow) return;
        _populatingFollow = true;

        try
        {
            var previous = SelectedFollow?.Target ?? _initialFollowTarget;

            var choices = new List<FollowChoiceRow> { new(null, null) };

            var hints = GuestHintCache.Snapshot
                .Where(TargetFollowService.IsFollowable)
                .GroupBy(h => h.Mac ?? h.Id ?? h.Address, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(h => h.Kind switch { "wsl" => 0, "hyperv" => 1, "wsb" => 2, _ => 3 })
                .ToList();

            var matched = false;
            foreach (var hint in hints)
            {
                var target = FollowTarget.FromHint(hint);
                if (target is null) continue;

                var isCurrent = previous is not null && TargetFollowService.Matches(hint, previous);
                matched |= isCurrent;

                choices.Add(new FollowChoiceRow(target, hint));
            }

            if (previous is not null && !matched)
                choices.Add(new FollowChoiceRow(previous, null));

            FollowOptions.Clear();
            foreach (var choice in choices) FollowOptions.Add(choice);

            SelectedFollow = choices.FirstOrDefault(c =>
                (c.Target is null && previous is null)
                || (c.Target is not null && previous is not null
                    && TargetFollowService.TargetEquals(c.Target, previous)))
                ?? choices[0];
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"填充跟随目标失败：{ex.Message}");
        }
        finally
        {
            _populatingFollow = false;
        }

        UpdatePreview();
    }

    [RelayCommand]
    private async Task RefreshFollowAsync()
    {
        try
        {
            await GuestHintCache.RefreshNowAsync(force: true);
            PopulateFollowChoices();
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"刷新跟随目标失败：{ex.Message}");
        }
    }

    /// <summary>填充目标地址候选（缓存快照去重；缓存为空时兜底一次网关/ARP）。</summary>
    public void PopulateAddressHints()
    {
        try
        {
            var snapshot = GuestHintCache.Snapshot;
            var source = snapshot.Count > 0
                ? snapshot
                : NetworkHintService.FilterUnconfirmedGuests(NetworkHintService.GetHints());

            var sandboxMacs = new HashSet<string>(
                source.Where(h => h.Category.Contains("沙盒", StringComparison.Ordinal) && h.Mac is not null)
                    .Select(h => h.Mac!),
                StringComparer.OrdinalIgnoreCase);

            var merged = new List<AddressOptionRow>();
            var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenSandboxMacs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var hint in source)
            {
                // 沙盒同一 MAC 可能出现在多个虚拟交换机上，只保留一条
                if (hint.Mac is not null
                    && sandboxMacs.Contains(hint.Mac)
                    && !seenSandboxMacs.Add(hint.Mac))
                    continue;

                if (seenAddresses.Add(hint.Address))
                    merged.Add(new AddressOptionRow(hint));
            }

            AddressOptions.Clear();
            foreach (var option in merged) AddressOptions.Add(option);
        }
        catch (Exception ex)
        {
            LogService.Current?.Warn($"填充地址候选失败：{ex.Message}");
        }
    }

    /// <summary>用户从候选下拉选中一项：只把纯 IP 填进地址。</summary>
    public void ApplySelectedHint(AddressOptionRow? option)
    {
        if (option is null || _populatingFollow) return;
        if (SelectedFollow?.Target is not null) return; // 跟随状态下禁手改

        ConnectAddress = option.Address;
        UpdatePreview();
    }

    /// <summary>用户手输提交（Enter/失焦）：清洗并更新。</summary>
    public void SubmitConnectText(string text)
    {
        if (SelectedFollow?.Target is not null) return;
        if (string.Equals(ConnectAddress, text, StringComparison.Ordinal)) return;

        ConnectAddress = text.Trim();
        UpdatePreview();
    }

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

    // 输入变化 → 实时刷新等价命令预览
    partial void OnSelectedFamilyChanged(string value) => UpdatePreview();
    partial void OnListenAddressChanged(string value) => UpdatePreview();
    partial void OnListenPortChanged(double value) => UpdatePreview();
    partial void OnConnectAddressChanged(string value) => UpdatePreview();
    partial void OnConnectPortChanged(double value) => UpdatePreview();

    /// <summary>编辑模式时载入现有值。</summary>
    public void LoadValues()
    {
        if (_existing is null) return;

        SelectedFamily = _existing.Family;
        ListenAddress = _existing.NormalizedListen;
        ListenPort = Math.Clamp(_existing.ListenPort, 1, 65535);
        ConnectAddress = _existing.NormalizedConnect;
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
            ? Validator.InferFamily(ListenAddress, ConnectAddress)
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

        if (!Validator.TryNormalizeAddress(
                ConnectAddress, family.EndsWith("v6"), out var connect, out var connectError))
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

        Result = entry;
        ResultMeta = new EntryMeta
        {
            Name = Trimmed(Name),
            Group = Trimmed(Group),
            Note = Trimmed(Note),
            HasFirewallRule = CreateFirewallRule,
            CreatedAt = _existingMeta?.CreatedAt ?? DateTimeOffset.Now,
        };

        // 目标跟随：跟随项来自下拉选择；不跟随则清空跟随标识
        var follow = SelectedFollow?.Target;
        ResultMeta.FollowKind = follow?.Kind;
        ResultMeta.FollowId = follow?.Id;
        ResultMeta.FollowMac = follow?.Mac;
        ResultMeta.FollowLabel = follow?.Label;

        Confirmed = true;

        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private async Task TestConnectAsync()
    {
        TestResultText = "正在测试…";
        var (ok, detail) = await PortProbe.TestConnectAsync(
            ConnectAddress.Trim(), (int)Math.Clamp(ConnectPort, 1, 65535));
        TestResultText = detail;
    }

    private static string? Trimmed(string value)
    {
        var text = value.Trim();
        return text.Length == 0 ? null : text;
    }
}
