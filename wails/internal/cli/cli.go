// Package cli 移植自 C# Cli.cs 的只读 dump 模式：
// --dump 以文本/JSON 输出当前全部映射后退出（纯只读，不需要管理员）。

package cli

import (
	"fmt"
	"os"
	"strings"

	"portproxymanager/internal/elevation"
	"portproxymanager/internal/jsonutil"
	"portproxymanager/internal/meta"
	"portproxymanager/internal/models"
	"portproxymanager/internal/paths"
	"portproxymanager/internal/registryio"
)

// Options 命令行选项（对应 C# CliOptions）。
type Options struct {
	DataDir   string
	Dump      bool
	Hints     bool
	JSON      bool
	Out       string
	Tray      bool
	NoElevate bool
	Help      bool
}

// Parse 解析命令行。
func Parse(args []string) Options {
	var o Options
	for _, arg := range args {
		a := strings.TrimSpace(arg)
		switch {
		case a == "--dump":
			o.Dump = true
		case a == "--hints":
			o.Hints = true
		case a == "--json":
			o.JSON = true
		case a == "--tray":
			o.Tray = true
		case a == "--no-elevate":
			o.NoElevate = true
		case a == "-h" || a == "--help":
			o.Help = true
		case strings.HasPrefix(a, "--data-dir="):
			o.DataDir = strings.TrimPrefix(a, "--data-dir=")
		case a == "--data-dir" || a == "--data-dir=":
			// 不带值的形式：忽略（Go flag 已处理 --data-dir 值形式）
		case strings.HasPrefix(a, "--out="):
			o.Out = strings.TrimPrefix(a, "--out=")
		}
	}
	return o
}

// HelpText 帮助文本。
const HelpText = `PortProxy 管理器（Wails 版） — netsh portproxy 的图形界面封装

用法:
  portproxymanager.exe [选项]

选项:
  --data-dir=<路径>   指定数据目录（meta.json / settings.json / logs / backups）。
  --tray              启动后直接最小化到托盘。
  --dump              以文本输出当前全部映射后退出（只读，不需要管理员）。
  --hints             探测并输出全部目标地址候选后退出（只读，不需要管理员）。
  --json              与 --dump 一起使用，输出 JSON。
  --out=<文件>        与 --dump 一起使用，同时把结果写入文件。
  --no-elevate        不自动提权（GUI 只读，写入会提示需要管理员权限）。
  -h, --help          显示本帮助。
`

// Dump 执行只读导出，返回退出码。
func Dump(o Options) int {
	entries, readErr := registryio.ReadAll()
	pathsCtx, dirErr := paths.ResolveForRead(o.DataDir)

	var metaStore *meta.Store
	if pathsCtx != nil {
		metaStore, _ = meta.Load(pathsCtx.MetaFile)
	}

	var output string
	if o.JSON {
		output = buildJSON(entries, readErr, pathsCtx, dirErr, metaStore)
	} else {
		output = buildText(entries, readErr, pathsCtx, metaStore)
	}

	fmt.Println(output)
	if o.Out != "" {
		if err := os.WriteFile(o.Out, []byte(output), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, "写入文件失败："+err.Error())
			return 1
		}
	}
	if readErr != nil {
		return 1
	}
	return 0
}

type dumpEntry struct {
	Family         string  `json:"family"`
	Protocol       string  `json:"protocol"`
	ListenAddress  string  `json:"listenAddress"`
	ListenPort     int     `json:"listenPort"`
	ConnectAddress string  `json:"connectAddress"`
	ConnectPort    int     `json:"connectPort"`
	Key            string  `json:"key"`
	Name           *string `json:"name,omitempty"`
	Group          *string `json:"group,omitempty"`
	Note           *string `json:"note,omitempty"`
}

// dumpPayload 字段名与 C# 版 --dump --json 一致。
type dumpPayload struct {
	Ok            bool        `json:"ok"`
	Error         *string     `json:"error,omitempty"`
	Elevated      bool        `json:"elevated"`
	DataDir       *string     `json:"dataDir,omitempty"`
	DataDirSource *string     `json:"dataDirSource,omitempty"`
	DataDirError  *string     `json:"dataDirError,omitempty"`
	Entries       []dumpEntry `json:"entries"`
}

func buildJSON(entries []models.PortProxyEntry, readErr error, pathsCtx *paths.Context, dirErr error, metaStore *meta.Store) string {
	payload := dumpPayload{
		Ok:       readErr == nil,
		Elevated: elevation.IsElevated(),
		Entries:  []dumpEntry{},
	}
	if readErr != nil {
		msg := readErr.Error()
		payload.Error = &msg
	}
	if pathsCtx != nil {
		dir := pathsCtx.DataDir
		src := pathsCtx.SourceEnum()
		payload.DataDir = &dir
		payload.DataDirSource = &src
	} else if dirErr != nil {
		msg := dirErr.Error()
		payload.DataDirError = &msg
	}
	for _, e := range entries {
		de := dumpEntry{
			Family: e.Family, Protocol: e.Protocol,
			ListenAddress: e.NormalizedListen(), ListenPort: e.ListenPort,
			ConnectAddress: e.NormalizedConnect(), ConnectPort: e.ConnectPort,
			Key: e.Key(),
		}
		if metaStore != nil {
			if m := metaStore.Get(e.Key()); m != nil {
				de.Name, de.Group, de.Note = m.Name, m.Group, m.Note
			}
		}
		payload.Entries = append(payload.Entries, de)
	}
	data, _ := jsonutil.Serialize(payload)
	return string(data)
}

func buildText(entries []models.PortProxyEntry, readErr error, pathsCtx *paths.Context, metaStore *meta.Store) string {
	var sb strings.Builder
	sb.WriteString("PortProxy 映射\r\n")
	sb.WriteString("数据来源：注册表 HKLM\\" + registryio.RootPath + "\\<family>\\tcp\r\n")
	if pathsCtx != nil {
		sb.WriteString("数据目录：" + pathsCtx.DataDir + "（来源：" + pathsCtx.SourceLabel() + "）\r\n")
	} else {
		sb.WriteString("数据目录：不可用\r\n")
	}
	if elevation.IsElevated() {
		sb.WriteString("当前权限：管理员\r\n")
	} else {
		sb.WriteString("当前权限：普通用户（写入需要提权）\r\n")
	}
	if readErr != nil {
		sb.WriteString("读取错误：" + readErr.Error() + "\r\n")
	}
	sb.WriteString("\r\n")

	sb.WriteString(pad("类型", 10) + pad("协议", 8) + pad("监听", 26) + pad("目标", 26) + "名称\r\n")
	for _, e := range entries {
		name := ""
		if metaStore != nil {
			if m := metaStore.Get(e.Key()); m != nil && m.Name != nil {
				name = *m.Name
			}
		}
		sb.WriteString(pad(e.Family, 10) + pad(e.Protocol, 8) + pad(e.ListenText(), 26) + pad(e.ConnectText(), 26) + name + "\r\n")
	}
	sb.WriteString(fmt.Sprintf("\r\n共 %d 条映射。\r\n", len(entries)))
	return sb.String()
}

// pad 按显示宽度右补空格（中文按 2 宽计算）。
func pad(s string, width int) string {
	w := 0
	for _, r := range s {
		if r > 0x2E80 { // CJK 区
			w += 2
		} else {
			w++
		}
	}
	if w >= width {
		return s + " "
	}
	return s + strings.Repeat(" ", width-w)
}
