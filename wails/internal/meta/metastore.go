// Package meta 移植自 C# Services/MetaStore.cs：
// meta.json 的读写。里面的 name / group / note 只用于界面展示，
// 绝不会拼进任何命令行——即便该文件被低权限用户篡改也无法造成命令注入。
package meta

import (
	"os"
	"strings"
	"sync"
	"time"

	"portproxymanager/internal/jsonutil"
	"portproxymanager/internal/logging"
	"portproxymanager/internal/models"
)

// document meta.json 的文档结构（与 C# Document 对应，camelCase 兼容）。
type document struct {
	Version int                          `json:"version"`
	Entries map[string]*models.EntryMeta `json:"entries"`
}

// Store meta 存储器。内部存指针，保证 GetOrCreate 返回的引用与后续 Save 一致（对应 C# 引用语义）。
type Store struct {
	mu    sync.Mutex
	file  string
	byKey map[string]*models.EntryMeta
}

// Load 读取 meta.json；文件坏了备份为 .broken 后从空开始（不能拖死启动）。
func Load(file string) (*Store, error) {
	s := &Store{file: file, byKey: map[string]*models.EntryMeta{}}
	var doc document
	if err := jsonutil.Read(file, &doc); err != nil {
		// 备份坏文件后从空开始
		if log := logging.Current(); log != nil {
			log.Error("meta.json 解析失败，已备份为 .broken：" + err.Error())
		}
		quarantine(file)
		return s, nil
	}
	for k, v := range doc.Entries {
		key := strings.TrimSpace(k)
		if key == "" {
			continue
		}
		if v == nil {
			v = &models.EntryMeta{}
		}
		s.byKey[key] = v
	}
	return s, nil
}

func quarantine(file string) {
	if _, err := os.Stat(file); err != nil {
		return
	}
	stamp := time.Now().Format("20060102-150405")
	_ = os.Rename(file, file+".broken-"+stamp)
}

// Count 条数。
func (s *Store) Count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.byKey)
}

// Get 查询单条；返回副本，供只读展示。没有时返回 nil。
func (s *Store) Get(key string) *models.EntryMeta {
	s.mu.Lock()
	defer s.mu.Unlock()
	if v, ok := s.byKey[key]; ok && v != nil {
		m := *v
		return &m
	}
	return nil
}

// GetOrCreate 查询或创建。返回内部存储的指针（与 C# 返回引用语义一致，
// 调用方改完调 Save 即落盘）。
func (s *Store) GetOrCreate(key string) *models.EntryMeta {
	s.mu.Lock()
	defer s.mu.Unlock()
	if v, ok := s.byKey[key]; ok && v != nil {
		return v
	}
	m := &models.EntryMeta{}
	s.byKey[key] = m
	return m
}

// Set 设置一条（拷贝传入值；只改内存，落盘要调 Save）。
func (s *Store) Set(key string, m models.EntryMeta) {
	s.mu.Lock()
	defer s.mu.Unlock()
	v := m
	s.byKey[key] = &v
}

// Remove 删除一条（只改内存，落盘要调 Save）。
func (s *Store) Remove(key string) bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	if _, ok := s.byKey[key]; !ok {
		return false
	}
	delete(s.byKey, key)
	return true
}

// Save 落盘。失败只记日志，不打断用户操作。
func (s *Store) Save() error {
	s.mu.Lock()
	doc := document{Version: 1, Entries: make(map[string]*models.EntryMeta, len(s.byKey))}
	for k, v := range s.byKey {
		doc.Entries[k] = v
	}
	s.mu.Unlock()

	if err := jsonutil.Write(s.file, doc); err != nil {
		if log := logging.Current(); log != nil {
			log.Warn("保存 meta.json 失败：" + err.Error())
		}
		return err
	}
	return nil
}

// FindOrphans 找出没有对应注册表条目的 meta 键（用于清理）。
func (s *Store) FindOrphans(entries []models.PortProxyEntry) []string {
	live := map[string]bool{}
	for _, e := range entries {
		live[strings.ToLower(e.Key())] = true
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	var orphans []string
	for k := range s.byKey {
		if !live[strings.ToLower(k)] {
			orphans = append(orphans, k)
		}
	}
	return orphans
}
