package meta

import (
	"os"
	"path/filepath"
	"testing"
)

// C# 版序列化的真实样式（camelCase、null 字段省略、DateTimeOffset 带时区偏移）。
const csharpStyleMeta = `{
  "version": 1,
  "entries": {
    "v4tov4|tcp|*|8080": {
      "name": "内网服务器",
      "group": "开发",
      "note": null,
      "hasFirewallRule": false,
      "createdAt": "2026-01-15T09:30:00.1234567+08:00",
      "followKind": "wsl",
      "followId": "Ubuntu-22.04"
    }
  }
}`

func TestCSharpJSONCompat(t *testing.T) {
	dir := t.TempDir()
	file := filepath.Join(dir, "meta.json")
	if err := os.WriteFile(file, []byte(csharpStyleMeta), 0o644); err != nil {
		t.Fatal(err)
	}
	s, err := Load(file)
	if err != nil {
		t.Fatalf("load: %v", err)
	}
	if s.Count() != 1 {
		t.Fatalf("count = %d, want 1", s.Count())
	}
	m := s.Get("v4tov4|tcp|*|8080")
	if m == nil {
		t.Fatal("meta 条目未读到")
	}
	if m.Name == nil || *m.Name != "内网服务器" {
		t.Errorf("name 读取错误: %v", m.Name)
	}
	if m.Note != nil {
		t.Errorf("note 应为 nil")
	}
	if !m.IsFollowing() || m.FollowId == nil || *m.FollowId != "Ubuntu-22.04" {
		t.Errorf("follow 字段读取错误")
	}
	if m.CreatedAt == nil || m.CreatedAt.Year() != 2026 {
		t.Errorf("createdAt 解析错误: %v", m.CreatedAt)
	}
}

func TestQuarantineBrokenFile(t *testing.T) {
	dir := t.TempDir()
	file := filepath.Join(dir, "meta.json")
	if err := os.WriteFile(file, []byte("{not json"), 0o644); err != nil {
		t.Fatal(err)
	}
	s, err := Load(file)
	if err != nil {
		t.Fatalf("load 不应报错: %v", err)
	}
	if s.Count() != 0 {
		t.Errorf("坏文件后应为空: %d", s.Count())
	}
	entries, _ := os.ReadDir(dir)
	found := false
	for _, e := range entries {
		if filepath.Ext(e.Name()) == ".broken-20260101-000000" || len(e.Name()) > len("meta.json") {
			found = true
		}
	}
	if !found {
		t.Errorf("坏文件应被隔离备份")
	}
}
