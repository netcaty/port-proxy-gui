// Package cli --hints 命令：探测并输出全部目标地址候选后退出。
// 输出结构对照 C# Cli.Hints（JSON 字段一致，文本表格同列）。
package cli

import (
	"fmt"
	"os"
	"sort"
	"strings"
	"time"

	"portproxymanager/internal/elevation"
	"portproxymanager/internal/hints"
	"portproxymanager/internal/jsonutil"
)

// hintCandidate JSON 里的一条候选（指针字段 null 时省略，与 C# WhenWritingNull 一致）。
type hintCandidate struct {
	Kind     *string `json:"kind,omitempty"`
	Address  string  `json:"address"`
	Category string  `json:"category"`
	ID       *string `json:"id,omitempty"`
	Mac      *string `json:"mac,omitempty"`
	Source   *string `json:"source,omitempty"`
	Detail   *string `json:"detail,omitempty"`
	Display  string  `json:"display"`
}

// hintsPayload JSON 载荷（与 C# payload 字段一致）。
type hintsPayload struct {
	Ok         bool            `json:"ok"`
	Error      *string         `json:"error,omitempty"`
	Elevated   bool            `json:"elevated"`
	Count      int             `json:"count"`
	ElapsedMs  int64           `json:"elapsedMs"`
	Candidates []hintCandidate `json:"candidates"`
}

// Hints 执行探测并输出，返回退出码（成功 0，探测失败 5）。
func Hints(o Options) int {
	start := time.Now()
	// RefreshNow 内部已兜住子任务异常，不会带 error 冒泡
	list := hints.RefreshNow(true)
	elapsed := time.Since(start).Milliseconds()

	var output string
	if o.JSON {
		payload := hintsPayload{
			Ok:         true,
			Elevated:   elevation.IsElevated(),
			Count:      len(list),
			ElapsedMs:  elapsed,
			Candidates: []hintCandidate{},
		}
		for _, h := range list {
			payload.Candidates = append(payload.Candidates, hintCandidate{
				Kind:     h.Kind,
				Address:  h.Address,
				Category: h.Category,
				ID:       h.ID,
				Mac:      h.Mac,
				Source:   h.Source,
				Detail:   h.Detail,
				Display:  h.Display,
			})
		}
		data, err := jsonutil.Serialize(payload)
		if err != nil {
			fmt.Fprintln(os.Stderr, "序列化失败："+err.Error())
			return 5
		}
		output = string(data)
	} else {
		output = buildHintsText(list, elapsed)
	}

	if o.JSON {
		fmt.Println(output) // C# JSON 用 AppendLine，末尾有一个换行
	} else {
		fmt.Print(output) // 文本内容已自带 CRLF，与 C# SafeWrite 一致
	}
	if o.Out != "" {
		if err := os.WriteFile(o.Out, []byte(output), 0o644); err != nil {
			fmt.Fprintln(os.Stderr, "写入 --out 文件失败："+err.Error())
			return 4
		}
	}
	return 0
}

// buildHintsText 文本表格（列与 C# 一致：类型/地址/标识名称/MAC/说明）。
func buildHintsText(list []hints.AddressHint, elapsedMs int64) string {
	var sb strings.Builder
	sb.WriteString("PortProxy 目标地址候选\r\n")
	sb.WriteString(fmt.Sprintf("探测耗时：%d ms\r\n", elapsedMs))
	if elevation.IsElevated() {
		sb.WriteString("当前权限：管理员\r\n")
	} else {
		sb.WriteString("当前权限：普通用户\r\n")
	}
	sb.WriteString("\r\n")

	type row [5]string
	rows := []row{{"类型", "地址", "标识/名称", "MAC", "说明"}}
	for _, h := range list {
		var noteParts []string
		for _, p := range []*string{&h.Category, h.Source, h.Detail} {
			if p != nil && strings.TrimSpace(*p) != "" {
				noteParts = append(noteParts, *p)
			}
		}
		kind := "?"
		if h.Kind != nil {
			kind = *h.Kind
		}
		id, mac := "", ""
		if h.ID != nil {
			id = *h.ID
		}
		if h.Mac != nil {
			mac = *h.Mac
		}
		rows = append(rows, row{kind, h.Address, id, mac, strings.Join(noteParts, " · ")})
	}

	// 按显示宽度计算列宽（中文按 2）
	widths := [5]int{}
	for _, r := range rows {
		for c := 0; c < 5; c++ {
			if w := displayWidth(r[c]); w > widths[c] {
				widths[c] = w
			}
		}
	}
	for _, r := range rows {
		var line strings.Builder
		for c := 0; c < 5; c++ {
			line.WriteString(padDisplay(r[c], widths[c]+2))
		}
		sb.WriteString(strings.TrimRight(line.String(), " ") + "\r\n")
	}
	sb.WriteString(fmt.Sprintf("\r\n共 %d 条\r\n", len(list)))
	return sb.String()
}

func displayWidth(s string) int {
	w := 0
	for _, r := range s {
		if r > 0x2E80 { // CJK 区
			w += 2
		} else {
			w++
		}
	}
	return w
}

func padDisplay(s string, width int) string {
	w := displayWidth(s)
	if w >= width {
		return s + " "
	}
	return s + strings.Repeat(" ", width-w)
}

// SortHints 按地址排序（仅 --hints 输出稳定性用途）。
func SortHints(list []hints.AddressHint) {
	sort.Slice(list, func(i, j int) bool { return list[i].Address < list[j].Address })
}
