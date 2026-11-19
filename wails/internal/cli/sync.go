// 无人值守跟随同步相关的 CLI：
//   --sync          跑一轮目标跟随同步后退出（计划任务的动作，也可手动执行）
//   --install-sync  注册计划任务：登录时启动 + 每 5 分钟运行一次 --sync
//   --remove-sync   删除该计划任务
//
// 与 GUI 内的定时同步共用 follow.Sync 和 meta.json，并通过 syncguard
// 串行化，避免计划任务与 GUI 同时先删后加同一条映射。

package cli

import (
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"portproxymanager/internal/elevation"
	"portproxymanager/internal/follow"
	"portproxymanager/internal/hints"
	"portproxymanager/internal/jsonutil"
	"portproxymanager/internal/logging"
	"portproxymanager/internal/meta"
	"portproxymanager/internal/paths"
	"portproxymanager/internal/registryio"
	"portproxymanager/internal/syncguard"
	"portproxymanager/internal/synctask"
)

// syncPayload --sync --json 的输出，字段风格与 --dump 保持一致。
type syncPayload struct {
	Ok            bool              `json:"ok"`
	Busy          bool              `json:"busy,omitempty"`
	Error         *string           `json:"error,omitempty"`
	Elevated      bool              `json:"elevated"`
	DataDir       *string           `json:"dataDir,omitempty"`
	DataDirSource *string           `json:"dataDirSource,omitempty"`
	DataDirError  *string           `json:"dataDirError,omitempty"`
	Report        follow.SyncReport `json:"report"`
}

// Sync 执行一轮目标跟随同步。退出码：
//
//	0 = 成功（含无变化 / 未检测到 / 跳过）
//	1 = 读取失败或至少一条更新失败
//	2 = 目标已变化，但当前进程没有管理员权限（计划任务应配置为 HighestAvailable）
func Sync(o Options) int {
	pathsCtx, dirErr := paths.Resolve(o.DataDir)
	if dirErr != nil {
		msg := dirErr.Error()
		payload := syncPayload{
			Ok:           false,
			Error:        &msg,
			Elevated:     elevation.IsElevated(),
			DataDirError: &msg,
			Report:       follow.SyncReport{Items: []follow.SyncItem{}},
		}
		return emitSync(payload, buildSyncText(payload, nil), o, 1)
	}
	if err := pathsCtx.EnsureLayout(); err != nil {
		msg := "数据目录不可用：" + err.Error()
		payload := syncPayload{
			Ok:       false,
			Error:    &msg,
			Elevated: elevation.IsElevated(),
			DataDir:  &pathsCtx.DataDir,
			Report:   follow.SyncReport{Items: []follow.SyncItem{}},
		}
		return emitSync(payload, buildSyncText(payload, nil), o, 1)
	}

	// 计划任务是后台运行，日志目录失败不能影响同步本身
	_, _ = logging.Init(pathsCtx.LogDir)
	// 一次性进程的候选缓存每次都是空的，别让"地址候选更新"每 5 分钟刷一遍日志
	hints.SetQuietLogging(true)

	metaStore, err := meta.Load(pathsCtx.MetaFile)
	if err != nil {
		msg := "meta.json 加载失败：" + err.Error()
		payload := syncPayload{
			Ok:       false,
			Error:    &msg,
			Elevated: elevation.IsElevated(),
			DataDir:  &pathsCtx.DataDir,
			Report:   follow.SyncReport{Items: []follow.SyncItem{}},
		}
		return emitSync(payload, buildSyncText(payload, nil), o, 1)
	}

	entries, readErr := registryio.ReadAll()
	if readErr != nil {
		msg := "读取注册表失败：" + readErr.Error()
		if log := logging.Current(); log != nil {
			log.Error("无人值守跟随同步：" + msg)
		}
		payload := syncPayload{
			Ok:       false,
			Error:    &msg,
			Elevated: elevation.IsElevated(),
			DataDir:  &pathsCtx.DataDir,
			Report:   follow.SyncReport{Items: []follow.SyncItem{}},
		}
		src := pathsCtx.SourceEnum()
		payload.DataDirSource = &src
		return emitSync(payload, buildSyncText(payload, metaStore), o, 1)
	}

	src := pathsCtx.SourceEnum()
	payload := syncPayload{
		Ok:            true,
		Elevated:      elevation.IsElevated(),
		DataDir:       &pathsCtx.DataDir,
		DataDirSource: &src,
		Report:        follow.SyncReport{Items: []follow.SyncItem{}},
	}

	// GUI 与计划任务可能同时触发；另一个正在同步时直接跳过（它做的就是同一件事）
	guard, acquired := syncguard.TryAcquire()
	if !acquired {
		payload.Busy = true
		return emitSync(payload, buildSyncText(payload, metaStore), o, 0)
	}
	report := follow.Sync(entries, metaStore.Get, elevation.IsElevated(), false)
	guard.Release()

	payload.Report = report
	payload.Ok = report.FailedCount == 0

	if log := logging.Current(); log != nil {
		switch {
		case report.FailedCount > 0:
			log.Error(fmt.Sprintf("无人值守跟随同步：更新 %d，失败 %d，待提权 %d",
				report.UpdatedCount, report.FailedCount, report.NeedsElevationCount))
		case report.UpdatedCount > 0:
			log.Info(fmt.Sprintf("无人值守跟随同步：更新 %d 条映射", report.UpdatedCount))
		case report.NeedsElevationCount > 0:
			log.Warn("无人值守跟随同步：目标地址已变化，但当前进程没有管理员权限。")
		}
	}

	code := 0
	if report.FailedCount > 0 {
		code = 1
	} else if report.NeedsElevationCount > 0 {
		code = 2
	}
	return emitSync(payload, buildSyncText(payload, metaStore), o, code)
}

// InstallSync 注册无人值守同步计划任务。
func InstallSync(o Options) int {
	pathsCtx, err := paths.Resolve(o.DataDir)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 1
	}
	r := synctask.Enable(pathsCtx.DataDir, taskExecutable())
	fmt.Println(r.Message)
	if !r.Ok {
		return 1
	}
	return 0
}

// RemoveSync 删除无人值守同步计划任务。
func RemoveSync() int {
	r := synctask.Disable()
	fmt.Println(r.Message)
	if !r.Ok {
		return 1
	}
	return 0
}

// taskExecutable 计划任务的动作程序：优先同目录 GUI exe（windowsgui 子系统，
// 后台运行不会闪黑窗）；找不到才退回当前进程（CLI 伴生程序本身）。
func taskExecutable() string {
	exe, err := os.Executable()
	if err != nil {
		return ""
	}
	gui := filepath.Join(filepath.Dir(exe), "portproxymanager.exe")
	if info, statErr := os.Stat(gui); statErr == nil && !info.IsDir() {
		return gui
	}
	return exe
}

func buildSyncText(p syncPayload, metaStore *meta.Store) string {
	var sb strings.Builder
	sb.WriteString("PortProxy 跟随同步\r\n")
	if p.DataDir != nil {
		src := ""
		if p.DataDirSource != nil {
			src = "（来源：" + sourceLabel(*p.DataDirSource) + "）"
		}
		sb.WriteString("数据目录：" + *p.DataDir + src + "\r\n")
	}
	if p.Elevated {
		sb.WriteString("当前权限：管理员\r\n")
	} else {
		sb.WriteString("当前权限：普通用户（只检查，不写入）\r\n")
	}
	if p.Error != nil {
		sb.WriteString("错误：" + *p.Error + "\r\n")
	}
	if p.Busy {
		sb.WriteString("\r\n另一个跟随同步正在进行，本次跳过。\r\n")
		return sb.String()
	}

	r := p.Report
	upToDate := 0
	for _, item := range r.Items {
		if item.Status == follow.StatusUpToDate {
			upToDate++
		}
	}
	sb.WriteString(fmt.Sprintf("\r\n结果：已更新 %d · 无变化 %d · 未检测到 %d · 待提权 %d · 失败 %d\r\n",
		r.UpdatedCount, upToDate, r.UnresolvedCount, r.NeedsElevationCount, r.FailedCount))

	if len(r.Items) == 0 {
		sb.WriteString("没有启用跟随的映射。\r\n")
		return sb.String()
	}
	sb.WriteString("\r\n")
	for _, item := range r.Items {
		sb.WriteString(fmt.Sprintf("[%s] %s：%s\r\n",
			item.StatusLabel, entryLabel(item, metaStore), item.Message))
	}
	return sb.String()
}

func entryLabel(item follow.SyncItem, metaStore *meta.Store) string {
	if metaStore != nil {
		if m := metaStore.Get(item.Entry.Key()); m != nil && m.Name != nil && strings.TrimSpace(*m.Name) != "" {
			return strings.TrimSpace(*m.Name)
		}
	}
	return item.Entry.ListenText() + " → " + item.Entry.ConnectText()
}

func sourceLabel(source string) string {
	switch source {
	case "CommandLine":
		return "--data-dir 指定"
	case "ExeDirectory":
		return "exe 同目录"
	case "ProgramData":
		return "ProgramData（已降级）"
	default:
		return source
	}
}

func emitSync(p syncPayload, text string, o Options, code int) int {
	output := text
	if o.JSON {
		if data, err := jsonutil.Serialize(p); err == nil {
			output = string(data)
		}
	}
	fmt.Println(output)
	if o.Out != "" {
		if err := os.WriteFile(o.Out, []byte(output), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, "写入文件失败："+err.Error())
			return 1
		}
	}
	return code
}
