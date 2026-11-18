package hints

// 探测超时（对应 C# ProbeTimeouts）。集中在此避免散落的魔法数字。
const (
	GuestProbeMS    = 6000
	ArpMS           = 4000
	DnsLookupMS     = 800
	DnsBatchSlackMS = 700
	SandboxNameMS   = 4000
	SandboxPingMS   = 600
	WslProbeMS      = 12000
	WslListMS       = 3000
	HyperVMS        = 8000
	VmCliMS         = 8000
	VBoxListMS      = 3000
	VBoxInfoMS      = 2500
	VMwareListMS    = 3000
)

// UI 定时器常量。
const (
	HintRefreshMS      = 30_000
	FollowSyncMS       = 5 * 60 * 1000
	NetworkDebounceMS  = 3_000
	StartupSyncDelayMS = 4_000
)

// utilities.go 里的通用小工具。
