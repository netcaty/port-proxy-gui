package synctask

import (
	"encoding/xml"
	"io"
	"strings"
	"testing"
	"time"
)

func TestBuildXMLWellFormed(t *testing.T) {
	start := time.Date(2026, 11, 19, 12, 0, 0, 0, time.Local)
	text := buildXML(`C:\Apps\portproxymanager.exe`, `C:\Data & Logs`, start)

	// 声明是 UTF-16（落盘时会被转成 UTF-16LE），测试里按 UTF-8 字符串解析
	parseable := strings.Replace(text, `encoding="UTF-16"`, `encoding="UTF-8"`, 1)

	// 必须是合法 XML（schtasks 会拒绝格式错误的任务定义）
	dec := xml.NewDecoder(strings.NewReader(parseable))
	for {
		_, err := dec.Token()
		if err == io.EOF {
			break
		}
		if err != nil {
			t.Fatalf("任务 XML 不合法：%v", err)
		}
	}

	for _, want := range []string{
		"<Interval>PT5M</Interval>",
		"<RunLevel>HighestAvailable</RunLevel>",
		"<LogonTrigger>",
		"<TimeTrigger>",
		"2026-11-19T12:00:00",
		"--sync --no-elevate",
		`C:\Data &amp; Logs`,
	} {
		if !strings.Contains(text, want) {
			t.Errorf("任务 XML 缺少 %q", want)
		}
	}
	// 实测 schtasks 拒绝 <Duration>PT0S</Duration>；无限重复必须省略 Duration
	if strings.Contains(text, "<Duration>") {
		t.Errorf("任务 XML 不应包含 Duration（schtasks 拒绝 PT0S）")
	}
}
