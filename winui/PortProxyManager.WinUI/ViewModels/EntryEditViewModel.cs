using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using PortProxyManager.Models;
using PortProxyManager.Services;
using PortProxyManager.WinUI.Views;

namespace PortProxyManager.WinUI.ViewModels;

/// <summary>
/// 新增/编辑映射表单。旧版 EntryDialog 的表单逻辑：
/// family 推断、地址归一化校验、实时等价命令预览、完整 Validator 校验。
/// 候选地址探测与目标跟随选择器留到阶段二。
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
            // 目标跟随在阶段二接入；这里显式置空避免误写入跟随标识
            FollowKind = null,
            FollowId = null,
            FollowMac = null,
            FollowLabel = null,
        };
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
