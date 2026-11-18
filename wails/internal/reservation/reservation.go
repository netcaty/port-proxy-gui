// Package reservation 移植自 C# Services/PortReservationService.cs：
// 查询 Windows 的 TCP 端口排除区间。
//
// 这些区间由 Hyper-V / WSL / WinNAT 等在启动时占用（"管理的端口排除"），
// 落在区间内的端口任何程序都无法绑定——portproxy 也一样，表现为
// 映射配置存在、页面显示正常、服务也在跑，但监听器永远建不起来。
//
// 系统没有公开的查询 API，只能解析 netsh 的输出；数据行形如
//
//	开始端口    结束端口
//	    3390        3489
//
// 该格式与系统语言无关，只按"两个数字"匹配即可。
package reservation

import (
	"regexp"
	"strconv"
	"sync"
	"time"

	"portproxymanager/internal/netsh"
)

// Range 一个排除区间 [Start, End]。
type Range struct {
	Start int `json:"start"`
	End   int `json:"end"`
}

var (
	rangeRowRe = regexp.MustCompile(`^\s*(\d{1,5})\s+(\d{1,5})\s*\*?\s*$`)

	gate     sync.Mutex
	cached   []Range
	cachedAt time.Time
	ttl      = 60 * time.Second
)

// GetExcludedTcpRanges 查询排除区间（60 秒缓存）。
func GetExcludedTcpRanges(refresh bool) []Range {
	gate.Lock()
	if !refresh && time.Since(cachedAt) < ttl {
		out := cached
		gate.Unlock()
		return out
	}
	gate.Unlock()

	ranges := query()

	gate.Lock()
	cached = ranges
	cachedAt = time.Now()
	gate.Unlock()

	return ranges
}

// FindContaining 返回包含该端口的保留区间；不在任何区间内则返回 nil。
func FindContaining(port int) *Range {
	for _, r := range GetExcludedTcpRanges(false) {
		if port >= r.Start && port <= r.End {
			return &Range{Start: r.Start, End: r.End}
		}
	}
	return nil
}

func query() []Range {
	result := netsh.RunQuiet([]string{"int", "ipv4", "show", "excludedportrange", "protocol=tcp"}, 6000)
	if !result.Ok() {
		return []Range{}
	}

	var ranges []Range
	for _, line := range splitLines(result.StdOut) {
		m := rangeRowRe.FindStringSubmatch(line)
		if m == nil {
			continue
		}
		start, err1 := strconv.Atoi(m[1])
		end, err2 := strconv.Atoi(m[2])
		if err1 != nil || err2 != nil || start <= 0 || end < start {
			continue
		}
		ranges = append(ranges, Range{Start: start, End: end})
	}
	if ranges == nil {
		ranges = []Range{}
	}
	return ranges
}

// splitLines 按 \r\n / \n / \r 切分并去掉空行。
func splitLines(s string) []string {
	var out []string
	start := 0
	for i := 0; i < len(s); i++ {
		if s[i] == '\n' || s[i] == '\r' {
			if i > start {
				out = append(out, s[start:i])
			}
			start = i + 1
			// \r\n 当一个分隔符
			if s[i] == '\r' && i+1 < len(s) && s[i+1] == '\n' {
				i++
				start = i + 1
			}
		}
	}
	if start < len(s) {
		out = append(out, s[start:])
	}
	return out
}
