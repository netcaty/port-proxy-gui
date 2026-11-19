package main

// AppService 是暴露给前端的唯一后端入口（Wails 服务绑定）。
// 职责：把 internal 各包的能力组合成前端需要的粗粒度方法，
// 并把注册表变更等后端事件推送给前端。

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/wailsapp/wails/v3/pkg/application"

	"portproxymanager/internal/autorun"
	"portproxymanager/internal/backup"
	"portproxymanager/internal/diagnoses"
	"portproxymanager/internal/elevation"
	"portproxymanager/internal/firewall"
	"portproxymanager/internal/follow"
	"portproxymanager/internal/hints"
	"portproxymanager/internal/iphelper"
	"portproxymanager/internal/logging"
	"portproxymanager/internal/meta"
	"portproxymanager/internal/models"
	"portproxymanager/internal/netsh"
	"portproxymanager/internal/paths"
	"portproxymanager/internal/registryio"
	"portproxymanager/internal/settings"
	"portproxymanager/internal/validator"
)

// AppName 应用名（窗口标题与日志用）。
const AppName = "PortProxyManager"

// AppVersion 版本号（跟随仓库 release 版本）。
const AppVersion = "2.1.0"

// EntryRow 主表格一行 = 注册表条目 + meta 合并视图。
type EntryRow struct {
	// 注册表真相源字段
	Family         string `json:"family"`
	Protocol       string `json:"protocol"`
	ListenAddress  string `json:"listenAddress"`
	ListenPort     int    `json:"listenPort"`
	ConnectAddress string `json:"connectAddress"`
	ConnectPort    int    `json:"connectPort"`

	// 派生
	Key         string `json:"key"`
	ListenText  string `json:"listenText"`
	ConnectText string `json:"connectText"`
	NetshCmd    string `json:"netshCmd"`

	// meta 补充（可为 null）
	Name            *string    `json:"name"`
	Group           *string    `json:"group"`
	Note            *string    `json:"note"`
	CreatedAt       *time.Time `json:"createdAt"`
	FollowKind      *string    `json:"followKind"`
	FollowId        *string    `json:"followId"`
	FollowMac       *string    `json:"followMac"`
	FollowLabel     *string    `json:"followLabel"`
	HasFirewallRule bool       `json:"hasFirewallRule"`

	// 跟随状态展示（阶段2 实装，先占位）
	FollowState string `json:"followState"`
}

// MetaInput 新增/编辑对话框提交的 meta 部分。
type MetaInput struct {
	Name  *string `json:"name"`
	Group *string `json:"group"`
	Note  *string `json:"note"`
	// AddFirewallRule 对话框"同时创建入站放行规则（TCP）"勾选。
	// 只有校验过的监听端口进规则，规则名固定由监听地址+端口生成。
	AddFirewallRule bool `json:"addFirewallRule"`
}

// MutationResult 写操作结果。
type MutationResult struct {
	Ok    bool      `json:"ok"`
	Error *string   `json:"error"`
	Entry *EntryRow `json:"entry"`
}

// AppService 绑定给前端的服务。
type AppService struct {
	paths    *paths.Context
	meta     *meta.Store
	settings *settings.Store
	watcher  *registryio.Watcher
	app      *application.App
	mu       sync.Mutex // 序列化写操作（netsh 写 + meta 变更）

	followStop chan struct{} // 目标跟随定时器停止信号
}

// NewAppService 组装服务。
func NewAppService(p *paths.Context, m *meta.Store, s *settings.Store) *AppService {
	return &AppService{paths: p, meta: m, settings: s, followStop: make(chan struct{})}
}

// SetApp 供 main 在 application.New 之后注入引用（用于发事件）。
func (a *AppService) SetApp(app *application.App) { a.app = app }

// StartWatcher 启动注册表监听：外部改动 → 界面自动刷新。
// 同时后台预热地址候选缓存、启动目标跟随定时器（对应 C# 启动 4s 首同步 + 5min 轮询）。
func (a *AppService) StartWatcher() {
	a.watcher = registryio.NewWatcher(func() {
		a.emitEntriesChanged()
	})
	a.watcher.Start()

	// 后台预热候选（下拉打开时只读快照，不阻塞界面）
	go hints.RefreshIfStale(true)

	// 启动 4 秒后首次跟随同步，之后每 5 分钟一次
	go a.followLoop()
}

// followLoop 定时跟随同步。
func (a *AppService) followLoop() {
	select {
	case <-time.After(hints.StartupSyncDelayMS * time.Millisecond):
	case <-a.followStop:
		return
	}
	a.syncFollowInternal(false)

	ticker := time.NewTicker(hints.FollowSyncMS * time.Millisecond)
	defer ticker.Stop()
	for {
		select {
		case <-ticker.C:
			a.syncFollowInternal(false)
		case <-a.followStop:
			return
		}
	}
}

// Stop 停止后台 goroutine（注册窗口关闭时调用）。
func (a *AppService) Stop() {
	select {
	case <-a.followStop:
	default:
		close(a.followStop)
	}
	if a.watcher != nil {
		a.watcher.Stop()
	}
}

func (a *AppService) emitEntriesChanged() {
	if a.app != nil {
		a.app.Event.Emit("entries-changed")
	}
}

// ServiceName Wails 服务元信息。
func (a *AppService) ServiceName() string { return "AppService" }

// ServiceStartup Wails 服务生命周期。
func (a *AppService) ServiceStartup(ctx context.Context, options map[string]any) error { return nil }

// ServiceShutdown Wails 服务生命周期。
func (a *AppService) ServiceShutdown() {}

// ---- 查询 ----

// ListEntries 主表格数据：注册表快照 + meta 合并。
func (a *AppService) ListEntries() ([]EntryRow, error) {
	entries, err := registryio.ReadAll()
	if err != nil {
		return nil, err
	}
	rows := make([]EntryRow, 0, len(entries))
	for _, e := range entries {
		rows = append(rows, buildRow(e, a.meta.Get(e.Key())))
	}
	return rows, nil
}

func buildRow(e models.PortProxyEntry, m *models.EntryMeta) EntryRow {
	row := EntryRow{
		Family:          e.Family,
		Protocol:        e.Protocol,
		ListenAddress:   e.ListenAddress,
		ListenPort:      e.ListenPort,
		ConnectAddress:  e.ConnectAddress,
		ConnectPort:     e.ConnectPort,
		Key:             e.Key(),
		ListenText:      e.ListenText(),
		ConnectText:     e.ConnectText(),
		NetshCmd:        e.NetshPreview(),
		HasFirewallRule: m != nil && m.HasFirewallRule,
	}
	if m != nil {
		row.Name = m.Name
		row.Group = m.Group
		row.Note = m.Note
		row.CreatedAt = m.CreatedAt
		row.FollowKind = m.FollowKind
		row.FollowId = m.FollowId
		row.FollowMac = m.FollowMac
		row.FollowLabel = m.FollowLabel
	}
	if row.FollowKind != nil && strings.TrimSpace(*row.FollowKind) != "" {
		row.FollowState = "已启用"
	}
	return row
}

// ListGroups 现有分组名（用于分组过滤下拉）。
func (a *AppService) ListGroups() []string {
	entries, err := registryio.ReadAll()
	if err != nil {
		return []string{}
	}
	set := map[string]bool{}
	for _, e := range entries {
		if m := a.meta.Get(e.Key()); m != nil && m.Group != nil && strings.TrimSpace(*m.Group) != "" {
			set[*m.Group] = true
		}
	}
	out := make([]string, 0, len(set))
	for g := range set {
		out = append(out, g)
	}
	sort.Strings(out)
	return out
}

// ListCandidates 目标地址候选列表（下拉用；只读快照，不启动探测）。
// refresh=true 时强刷一次并等待（用于"刷新候选"按钮）。
func (a *AppService) ListCandidates(refresh bool) []hints.AddressHint {
	var list []hints.AddressHint
	if refresh {
		list = hints.RefreshNow(true)
	} else {
		list = hints.Snapshot()
		if len(list) == 0 {
			// 缓存还没预热好，强刷一次
			list = hints.RefreshNow(false)
		}
	}
	// 兜底：确保每条都有 display 文本（前端下拉依赖该字段）
	out := make([]hints.AddressHint, len(list))
	for i, h := range list {
		if h.Display == "" {
			h.Display = h.DisplayText()
		}
		out[i] = h
	}
	return out
}

// SetFollowTarget 给一条映射设置/清除跟随目标（只改 meta，不碰 netsh）。
func (a *AppService) SetFollowTarget(key string, kind string, id string, mac string, label string) MutationResult {
	a.mu.Lock()
	defer a.mu.Unlock()

	m := a.meta.GetOrCreate(key)
	if strings.TrimSpace(kind) == "" {
		// 清除跟随
		m.FollowKind, m.FollowId, m.FollowMac, m.FollowLabel = nil, nil, nil, nil
	} else {
		m.FollowKind = strPtrLocal(strings.ToLower(strings.TrimSpace(kind)))
		m.FollowId = strPtrLocal(strings.TrimSpace(id))
		m.FollowMac = strPtrLocal(strings.TrimSpace(mac))
		m.FollowLabel = strPtrLocal(strings.TrimSpace(label))
	}
	if m.CreatedAt == nil {
		now := time.Now()
		m.CreatedAt = &now
	}
	_ = a.meta.Save()

	a.emitEntriesChanged()
	return MutationResult{Ok: true}
}

// SyncFollow 手动触发一次目标跟随同步（force=true 时强刷候选）。
func (a *AppService) SyncFollow(force bool) follow.SyncReport {
	return a.syncFollowInternal(force)
}

func (a *AppService) syncFollowInternal(force bool) follow.SyncReport {
	entries, err := registryio.ReadAll()
	if err != nil {
		return follow.SyncReport{Items: []follow.SyncItem{}}
	}
	report := follow.Sync(entries, func(key string) *models.EntryMeta {
		return a.meta.Get(key)
	}, elevation.IsElevated(), force)
	if len(report.Items) > 0 {
		a.emitEntriesChanged()
	}
	return report
}

// ValidateEntry 前端表单校验入口。
func (a *AppService) ValidateEntry(entry models.PortProxyEntry, editingKey string) validator.Result {
	existing, _ := registryio.ReadAll()
	return validator.Validate(entry, existing, editingKey)
}

// PreviewNetsh 等价 netsh 命令预览。
func (a *AppService) PreviewNetsh(entry models.PortProxyEntry) string {
	return entry.NetshPreview()
}

// InferFamily 根据两侧地址推断 family（前端表单预览用）。
func (a *AppService) InferFamily(listenAddress, connectAddress string) string {
	return validator.InferFamily(listenAddress, connectAddress)
}

// ---- 写操作（互斥） ----

// AddEntry 新增映射 + meta。
func (a *AppService) AddEntry(entry models.PortProxyEntry, metaIn MetaInput) MutationResult {
	a.mu.Lock()
	defer a.mu.Unlock()

	existing, _ := registryio.ReadAll()
	if res := validator.Validate(entry, existing, ""); !res.Ok {
		return mutationFail(*res.Error)
	}

	created, err := netsh.Add(entry)
	if err != nil {
		return mutationFail(err.Error())
	}

	m := metaFromInput(metaIn)
	if m.CreatedAt == nil {
		now := time.Now()
		m.CreatedAt = &now
	}
	a.meta.Set(created.Key(), *m)
	_ = a.meta.Save()

	// 防火墙联动（对照 C# MainForm 新增流程）：勾选时加规则，
	// 加失败报"映射已创建，但防火墙规则添加失败"——映射保留，meta 标记
	// 已存 true，诊断第 10 步会提示缺规则并给修复按钮。
	if metaIn.AddFirewallRule {
		if r := firewall.Add(created); !r.Ok {
			a.emitEntriesChanged()
			return MutationResult{Ok: false, Error: strPtrLocal("映射已创建，但防火墙规则添加失败：" + r.Message)}
		}
	}

	a.emitEntriesChanged()
	row := buildRow(created, a.meta.Get(created.Key()))
	return MutationResult{Ok: true, Entry: &row}
}

// UpdateEntry 编辑映射 = netsh 先删后加 + meta 键迁移。
func (a *AppService) UpdateEntry(oldEntry models.PortProxyEntry, newEntry models.PortProxyEntry, metaIn MetaInput) MutationResult {
	a.mu.Lock()
	defer a.mu.Unlock()

	existing, _ := registryio.ReadAll()
	if res := validator.Validate(newEntry, existing, oldEntry.Key()); !res.Ok {
		return mutationFail(*res.Error)
	}

	// 破坏性操作前自动快照
	a.snapshotIfConfigured("修改前")

	if _, err := netsh.Update(oldEntry, newEntry); err != nil {
		return mutationFail(err.Error())
	}

	// meta 搬家：删旧键、设新键（与 C# MainForm 编辑流程一致）
	m := metaFromInput(metaIn)
	if m.CreatedAt == nil {
		if old := a.meta.Get(oldEntry.Key()); old != nil && old.CreatedAt != nil {
			m.CreatedAt = old.CreatedAt
		} else {
			now := time.Now()
			m.CreatedAt = &now
		}
	}
	// 防火墙联动（对照 C# MainForm 编辑流程）：
	//   · 旧 meta 标记了规则 → 删旧条目的规则（规则名由旧监听地址+端口生成）
	//   · 勾选 → 为新条目加规则（即使监听未变也幂等重建，保证勾选与实体一致）
	oldMeta := a.meta.Get(oldEntry.Key())
	a.meta.Remove(oldEntry.Key())
	a.meta.Set(newEntry.Key(), *m)
	_ = a.meta.Save()

	if oldMeta != nil && oldMeta.HasFirewallRule {
		r := firewall.Delete(oldEntry)
		if log := logging.Current(); log != nil {
			log.Info("编辑后清理旧防火墙规则：" + r.Message + "（" + mapOk(r.Ok) + "）")
		}
	}
	if metaIn.AddFirewallRule {
		if r := firewall.Add(newEntry); !r.Ok {
			a.emitEntriesChanged()
			return MutationResult{Ok: false, Error: strPtrLocal("映射已更新，但防火墙规则添加失败：" + r.Message)}
		}
	}

	a.emitEntriesChanged()
	row := buildRow(newEntry, a.meta.Get(newEntry.Key()))
	return MutationResult{Ok: true, Entry: &row}
}

// DeleteEntry 删除映射 + meta。
func (a *AppService) DeleteEntry(entry models.PortProxyEntry) MutationResult {
	a.mu.Lock()
	defer a.mu.Unlock()

	// 破坏性操作前自动快照（失败不影响主流程）
	a.snapshotIfConfigured("删除前")

	if err := netsh.Delete(entry, false); err != nil {
		return mutationFail(err.Error())
	}

	// 有防火墙规则的一并清理
	if m := a.meta.Get(entry.Key()); m != nil && m.HasFirewallRule {
		_ = firewall.Delete(entry)
	}
	a.meta.Remove(entry.Key())
	_ = a.meta.Save()

	a.emitEntriesChanged()
	return MutationResult{Ok: true}
}

// ---- meta / settings / 环境 ----

// GetMeta 读取单条 meta。
func (a *AppService) GetMeta(key string) *models.EntryMeta {
	m := a.meta.Get(key)
	return m
}

// GetSettings 读取界面偏好。
func (a *AppService) GetSettings() settings.AppSettings { return a.settings.Value }

// SaveSettings 保存界面偏好。
func (a *AppService) SaveSettings(s settings.AppSettings) MutationResult {
	a.settings.Value = s
	if err := a.settings.Save(); err != nil {
		return mutationFail("保存设置失败：" + err.Error())
	}
	return MutationResult{Ok: true}
}

// EnvInfo 环境信息（状态栏用）。
type EnvInfo struct {
	DataDir     string `json:"dataDir"`
	SourceLabel string `json:"sourceLabel"`
	Elevated    bool   `json:"elevated"`
	User        string `json:"user"`
	AppVersion  string `json:"appVersion"`
}

// GetEnv 环境信息。
func (a *AppService) GetEnv() EnvInfo {
	return EnvInfo{
		DataDir:     a.paths.DataDir,
		SourceLabel: a.paths.SourceLabel(),
		Elevated:    elevation.IsElevated(),
		User:        elevation.CurrentUser(),
		AppVersion:  AppVersion,
	}
}

// ---- 诊断 / 防火墙 / IP Helper ----

// Diagnose 对一条映射运行十步诊断。
func (a *AppService) Diagnose(entry models.PortProxyEntry) diagnoses.Report {
	return diagnoses.Run(entry, a.meta.Get(entry.Key()), elevation.IsElevated())
}

// FixDiagnostic 执行诊断项的修复动作。
func (a *AppService) FixDiagnostic(action diagnoses.Action, entry models.PortProxyEntry) diagnoses.FixResult {
	return diagnoses.ApplyFix(action, entry)
}

// GetIpHlpState 查询 IP Helper 服务状态。
func (a *AppService) GetIpHlpState() iphelper.State {
	return iphelper.GetState()
}

// StartIpHelper 启动 IP Helper。
func (a *AppService) StartIpHelper() iphelper.ActionResult { return iphelper.TryStart() }

// RestartIpHelper 重启 IP Helper。
func (a *AppService) RestartIpHelper() iphelper.ActionResult { return iphelper.TryRestart() }

// FirewallAdd 为映射添加防火墙规则。
func (a *AppService) FirewallAdd(entry models.PortProxyEntry) firewall.AddResult {
	return firewall.Add(entry)
}

// FirewallDelete 删除映射的防火墙规则。
func (a *AppService) FirewallDelete(entry models.PortProxyEntry) firewall.AddResult {
	return firewall.Delete(entry)
}

// ---- 备份 / 导入导出 ----

// ExportEntries 导出当前全部映射到文件（返回文件路径）。
func (a *AppService) ExportEntries(file string) MutationResult {
	entries, err := registryio.ReadAll()
	if err != nil {
		return mutationFail("读取映射失败：" + err.Error())
	}
	if err := backup.Export(file, entries, func(key string) *models.EntryMeta { return a.meta.Get(key) }); err != nil {
		return mutationFail("导出失败：" + err.Error())
	}
	return MutationResult{Ok: true}
}

// LoadImportDocument 读取导入文件并返回差异预览。
func (a *AppService) LoadImportDocument(file string) backup.Document {
	doc, err := backup.LoadDocument(file)
	if err != nil {
		return backup.Document{Version: 0, Entries: []backup.ExportEntry{}}
	}
	return doc
}

// PreviewImport 计算导入差异（不执行）。
func (a *AppService) PreviewImport(file string) backup.PreviewResult {
	doc, err := backup.LoadDocument(file)
	if err != nil {
		return backup.PreviewResult{Error: strPtrLocal(err.Error())}
	}
	entries, _ := registryio.ReadAll()
	diff := backup.Diff(entries, doc)
	return backup.PreviewResult{Document: &doc, Diff: diff}
}

// ApplyImport 执行导入差异（执行选中的条目）。
func (a *AppService) ApplyImport(items []backup.DiffItem) backup.ApplyResult {
	a.mu.Lock()
	defer a.mu.Unlock()
	entries, _ := registryio.ReadAll()
	result := backup.Apply(items, entries, a.meta, func(models.PortProxyEntry) {})
	a.emitEntriesChanged()
	return result
}

// CreateSnapshot 创建一份手动快照。
func (a *AppService) CreateSnapshot(tag string) MutationResult {
	entries, err := registryio.ReadAll()
	if err != nil {
		return mutationFail("读取映射失败：" + err.Error())
	}
	cfg := a.settings.Value
	_, err = backup.WriteSnapshot(a.paths.BackupDir, entries,
		func(key string) *models.EntryMeta { return a.meta.Get(key) }, tag, &cfg)
	if err != nil {
		return mutationFail("创建快照失败：" + err.Error())
	}
	return MutationResult{Ok: true}
}

// ListBackups 列出备份/导出文件。
func (a *AppService) ListBackups() []backup.BackupFile {
	return backup.ListFiles(a.paths.BackupDir)
}

// ---- 开机自启（计划任务） ----

// IsAutorunEnabled 查询开机自启状态。
func (a *AppService) IsAutorunEnabled() bool { return autorun.IsEnabled() }

// EnableAutorun 注册开机自启（计划任务，管理员权限，登录不弹 UAC）。
func (a *AppService) EnableAutorun() autorun.Result {
	return autorun.Enable(a.paths.DataDir)
}

// DisableAutorun 取消开机自启。
func (a *AppService) DisableAutorun() autorun.Result { return autorun.Disable() }

// OpenDataDir 用资源管理器打开数据目录。
func (a *AppService) OpenDataDir() MutationResult {
	cmd := exec.Command("explorer.exe", a.paths.DataDir)
	if err := cmd.Start(); err != nil {
		return mutationFail("打开数据目录失败：" + err.Error())
	}
	return MutationResult{Ok: true}
}

// ---- 日志查看 ----

// LogFile 日志文件条目。
type LogFile struct {
	Name string `json:"name"`
	Size int64  `json:"size"`
}

// ListLogs 列出日志文件（按名字倒序 = 新的在前）。
func (a *AppService) ListLogs() []LogFile {
	files, err := os.ReadDir(a.paths.LogDir)
	if err != nil {
		return []LogFile{}
	}
	var out []LogFile
	for _, f := range files {
		if f.IsDir() || !strings.HasPrefix(f.Name(), "app-") {
			continue
		}
		info, err := f.Info()
		if err != nil {
			continue
		}
		out = append(out, LogFile{Name: f.Name(), Size: info.Size()})
	}
	sort.Slice(out, func(i, j int) bool { return out[i].Name > out[j].Name })
	return out
}

// ReadLog 读一个日志文件全文（UTF-8）。
func (a *AppService) ReadLog(name string) (string, error) {
	// 只允许 app-YYYYMMDD.log 这种名字，防路径穿越
	if strings.ContainsAny(name, `/\`) || !strings.HasPrefix(name, "app-") || !strings.HasSuffix(name, ".log") {
		return "", fmt.Errorf("非法的日志文件名：%s", name)
	}
	data, err := os.ReadFile(a.paths.LogDir + string(os.PathSeparator) + name)
	if err != nil {
		return "", err
	}
	return string(data), nil
}

// ---- 辅助 ----

func metaFromInput(in MetaInput) *models.EntryMeta {
	m := &models.EntryMeta{}
	if strings.TrimSpace(deref(in.Name)) != "" {
		m.Name = in.Name
	}
	if strings.TrimSpace(deref(in.Group)) != "" {
		m.Group = in.Group
	}
	if strings.TrimSpace(deref(in.Note)) != "" {
		m.Note = in.Note
	}
	m.HasFirewallRule = in.AddFirewallRule
	return m
}

func mapOk(ok bool) string {
	if ok {
		return "成功"
	}
	return "失败"
}

func deref(s *string) string {
	if s == nil {
		return ""
	}
	return *s
}

func strPtrLocal(s string) *string {
	if s == "" {
		return nil
	}
	return &s
}

// snapshotIfConfigured 破坏性操作前的自动快照（失败静默，不打断主流程）。
func (a *AppService) snapshotIfConfigured(tag string) {
	entries, err := registryio.ReadAll()
	if err != nil {
		return
	}
	cfg := a.settings.Value
	_, _ = backup.WriteSnapshot(a.paths.BackupDir, entries,
		func(key string) *models.EntryMeta { return a.meta.Get(key) }, tag, &cfg)
}

func mutationFail(msg string) MutationResult {
	if log := logging.Current(); log != nil {
		log.Warn("前端操作失败：" + msg)
	}
	return MutationResult{Ok: false, Error: &msg}
}
