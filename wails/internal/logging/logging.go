// Package logging 移植自 C# Services/LogService.cs：
// 极简日志，每天一个文件，保留最近 7 天。任何 IO 异常都被吞掉——日志失败绝不能影响主流程。
package logging

import (
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"
)

var (
	mu      sync.Mutex
	current *Logger
)

// Current 全局实例，Init 之前为 nil，调用方需判空（与 C# 版 LogService.Current 语义一致）。
func Current() *Logger { return current }

type Logger struct {
	dir string
}

// Init 创建日志目录并设为全局实例，返回错误仅用于上层提示，绝不打断主流程。
func Init(logDir string) (*Logger, error) {
	if err := os.MkdirAll(logDir, 0o755); err != nil {
		return nil, err
	}
	l := &Logger{dir: logDir}
	mu.Lock()
	current = l
	mu.Unlock()
	l.PurgeOld(7)
	return l, nil
}

func (l *Logger) CurrentFile() string {
	return filepath.Join(l.dir, fmt.Sprintf("app-%s.log", time.Now().Format("20060102")))
}

func (l *Logger) Dir() string { return l.dir }

func (l *Logger) Info(message string)  { l.Write("INFO", message) }
func (l *Logger) Warn(message string)  { l.Write("WARN", message) }
func (l *Logger) Error(message string) { l.Write("ERROR", message) }

func (l *Logger) Write(level, message string) {
	if l == nil {
		return
	}
	line := fmt.Sprintf("%s [%s] %s\r\n", time.Now().Format("2006-01-02 15:04:05.000"), level, message)
	mu.Lock()
	defer mu.Unlock()
	appendUtf8(l.CurrentFile(), line) // 日志不可写时静默放弃
}

// PurgeOld 删除超过 keepDays 的旧日志。
func (l *Logger) PurgeOld(keepDays int) {
	if l == nil {
		return
	}
	entries, err := os.ReadDir(l.dir)
	if err != nil {
		return // 目录不可枚举时忽略
	}
	limit := time.Now().AddDate(0, 0, -keepDays)
	for _, e := range entries {
		if e.IsDir() {
			continue
		}
		full := filepath.Join(l.dir, e.Name())
		if info, err := e.Info(); err == nil && info.ModTime().Before(limit) {
			_ = os.Remove(full) // 单个文件删不掉不影响其他
		}
	}
}

func appendUtf8(path, text string) {
	f, err := os.OpenFile(path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return
	}
	defer f.Close()
	_, _ = f.WriteString(text)
}
