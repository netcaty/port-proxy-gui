namespace PortProxyManager.Services;

/// <summary>
/// 地址探测与跟随刷新用到的超时值。集中在这里，避免散落的魔法数字；
/// 实际调用仍按各自场景取较小值（例如 <c>Math.Min(timeoutMs, HyperVMs)</c>）。
/// </summary>
internal static class ProbeTimeouts
{
    public const int GuestProbeMs = 6000;
    public const int ArpMs = 4000;
    public const int DnsLookupMs = 800;
    public const int DnsBatchSlackMs = 700;
    public const int SandboxNameMs = 4000;
    public const int SandboxPingMs = 600;
    public const int WslProbeMs = 12000;
    public const int WslListMs = 3000;
    public const int HyperVMs = 8000;
    public const int VmCliMs = 8000;
    public const int VBoxListMs = 3000;
    public const int VBoxInfoMs = 2500;
    public const int VMwareListMs = 3000;

    // UI 定时器
    public const int HintRefreshMs = 30_000;
    public const int FollowSyncMs = 5 * 60 * 1000;
    public const int NetworkDebounceMs = 3_000;
    public const int StartupSyncDelayMs = 4_000;
    public const int StatusTimerMs = 1_500;
    public const int StatusRefreshDelayMs = 1_200;
}
