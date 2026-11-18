// Package jsonutil 移植自 C# Services/JsonIo.cs：
// 统一的 JSON 读写，保证与 C# 版数据文件（meta.json / settings.json）格式互相兼容——
// 缩进 2 空格、字段名 camelCase、中文不转义、UTF-8 无 BOM、原子替换。
package jsonutil

import (
	"bytes"
	"encoding/json"
	"os"
)

// Read 读取并反序列化；文件不存在返回 nil, nil（与 C# 版语义一致）。
func Read(path string, v any) error {
	data, err := os.ReadFile(path)
	if err != nil {
		if os.IsNotExist(err) {
			return nil
		}
		return err
	}
	trimmed := bytes.TrimSpace(data)
	if len(trimmed) == 0 {
		return nil
	}
	// Go 的 encoding/json 默认容忍未知字段，字段名匹配大小写不敏感，与 C# 版互通
	dec := json.NewDecoder(bytes.NewReader(trimmed))
	return dec.Decode(v)
}

// Serialize 序列化为与 C# 版一致的风格：2 空格缩进、不转义 HTML 字符。
func Serialize(v any) ([]byte, error) {
	var buf bytes.Buffer
	enc := json.NewEncoder(&buf)
	enc.SetEscapeHTML(false)
	enc.SetIndent("", "  ")
	if err := enc.Encode(v); err != nil {
		return nil, err
	}
	// json.Encoder 会在末尾补一个换行，去掉它以贴近 C# 输出
	out := buf.Bytes()
	if n := len(out); n > 0 && out[n-1] == '\n' {
		out = out[:n-1]
	}
	return out, nil
}

// Write 先写临时文件再替换，避免写到一半崩溃留下半个 JSON。
func Write(path string, v any) error {
	data, err := Serialize(v)
	if err != nil {
		return err
	}
	temp := path + ".tmp"
	if err := os.WriteFile(temp, data, 0o644); err != nil {
		return err
	}
	return os.Rename(temp, path)
}
