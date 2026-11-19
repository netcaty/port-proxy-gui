// Package synctask 注册「无人值守跟随同步」计划任务。
//
// 背景：GUI 的目标跟随定时器只在主程序运行时工作，退出后 WSL/虚拟机换 IP
// 就没人重建映射。这里注册一个计划任务：登录时启动、之后每 5 分钟运行一次
// `portproxymanager.exe --sync --no-elevate --data-dir=...`，由程序自己的
// 同步逻辑（meta.json 里的跟随规则）完成探测和重建。
//
// 与 autorun 一样用 schtasks /xml 注册，避开 /tr 的引号地狱：
//   - RunLevel=HighestAvailable + InteractiveToken：不弹 UAC
//   - 任务动作优先指向 GUI exe（windowsgui 子系统），计划任务运行时不会闪黑窗。
package synctask

import (
	"encoding/xml"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"golang.org/x/sys/windows"

	"portproxymanager/internal/elevation"
	"portproxymanager/internal/netsh"
	"portproxymanager/internal/proc"
	"portproxymanager/internal/windir"
)

// TaskName 计划任务名（与 GUI 开机自启的 PortProxyManager 区分）。
const TaskName = "PortProxyManagerFollowSync"

// Interval 同步间隔，与 GUI 定时器（hints.FollowSyncMS）保持一致。
const Interval = 5 * time.Minute

// Result 注册/删除动作的结果。
type Result struct {
	Ok      bool   `json:"ok"`
	Message string `json:"message"`
}

func schtasks() string { return windir.SystemDir() + string(os.PathSeparator) + "schtasks.exe" }

// IsEnabled 查询计划任务是否存在。
func IsEnabled() bool {
	res := proc.RunQuiet(schtasks(), []string{"/query", "/tn", TaskName}, proc.WithTimeoutMs(5000))
	return res.Ok()
}

// Enable 注册（或覆盖）无人值守同步计划任务。
// dataDir 通过 --data-dir 传给计划任务，保证和当前实例使用同一份 meta.json。
// exePath 为空时退回当前进程；CLI 调用方应优先传入同目录的 GUI exe
// （windowsgui 子系统运行时不闪控制台窗口）。
func Enable(dataDir, exePath string) Result {
	if !elevation.IsElevated() {
		return Result{Message: "注册无人值守跟随同步需要管理员权限。"}
	}

	if exePath == "" {
		exe, err := os.Executable()
		if err != nil {
			return Result{Message: "无法确定程序路径：" + err.Error()}
		}
		exePath = exe
	}
	if _, err := os.Stat(exePath); err != nil {
		return Result{Message: "找不到用于计划任务的程序：" + exePath}
	}

	// 时间触发器从 1 分钟后开始，注册后不必等到下次登录才生效。
	xmlText := buildXML(exePath, dataDir, time.Now().Add(time.Minute))
	temp := filepath.Join(os.TempDir(), fmt.Sprintf("ppm-sync-task-%d.xml", os.Getpid()))
	// schtasks 要求 XML 是 UTF-16 带 BOM
	if err := os.WriteFile(temp, append([]byte{0xFF, 0xFE}, utf16LE(xmlText)...), 0o600); err != nil {
		return Result{Message: "写临时 XML 失败：" + err.Error()}
	}
	defer os.Remove(temp)

	res, err := proc.Run(schtasks(), []string{"/create", "/tn", TaskName, "/xml", temp, "/f"}, proc.WithTimeoutMs(10000))
	if err != nil || !res.Ok() {
		return Result{Message: netsh.Friendly(res)}
	}
	if !IsEnabled() {
		return Result{Message: "计划任务创建命令返回成功，但查询不到该任务，请检查「任务计划程序」。"}
	}
	return Result{Ok: true, Message: "已注册无人值守跟随同步（计划任务「" + TaskName +
		"」，登录时启动，之后每 5 分钟检查一次；主程序退出后仍然生效）。"}
}

// Disable 删除计划任务。任务不存在也视为成功。
func Disable() Result {
	if !elevation.IsElevated() {
		return Result{Message: "删除无人值守跟随同步需要管理员权限。"}
	}
	res, err := proc.Run(schtasks(), []string{"/delete", "/tn", TaskName, "/f"}, proc.WithTimeoutMs(10000))
	if (err == nil && res.Ok()) || !IsEnabled() {
		return Result{Ok: true, Message: "已删除无人值守跟随同步计划任务。"}
	}
	return Result{Message: netsh.Friendly(res)}
}

func buildXML(exePath, dataDir string, start time.Time) string {
	user := xmlEscape(elevation.CurrentUser())
	exe := xmlEscape(exePath)
	args := xmlEscape(`--sync --no-elevate --data-dir="` + dataDir + `"`)
	boundary := start.Format("2006-01-02T15:04:05")
	// 省略 Duration = 无限重复。实测 schtasks 会拒绝 <Duration>PT0S</Duration>
	//（“任务 XML 包含格式不正确或超出范围的值”），不要加回来。
	repetition := `<Repetition>
        <Interval>PT5M</Interval>
        <StopAtDurationEnd>false</StopAtDurationEnd>
      </Repetition>`
	return fmt.Sprintf(`<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>PortProxy 管理器：无人值守目标跟随同步。登录时启动，之后每 5 分钟运行一次，仅在目标地址变化时重建映射。</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>%s</UserId>
      %s
    </LogonTrigger>
    <TimeTrigger>
      <StartBoundary>%s</StartBoundary>
      <Enabled>true</Enabled>
      %s
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>%s</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT10M</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>%s</Command>
      <Arguments>%s</Arguments>
    </Exec>
  </Actions>
</Task>`, user, repetition, boundary, repetition, user, exe, args)
}

func xmlEscape(s string) string {
	var b strings.Builder
	_ = xml.EscapeText(&b, []byte(s))
	return b.String()
}

func utf16LE(s string) []byte {
	u := windows.StringToUTF16(s)
	out := make([]byte, len(u)*2)
	for i, v := range u {
		out[2*i] = byte(v)
		out[2*i+1] = byte(v >> 8)
	}
	return out
}
