package paths

import (
	"path/filepath"
	"strings"
	"testing"
)

func TestRequestedPathAbsolute(t *testing.T) {
	base := `C:\Apps\PortProxy`
	abs := `D:\Data\PortProxy`
	got, err := requestedPath(base, abs)
	if err != nil {
		t.Fatal(err)
	}
	if got != abs {
		t.Errorf("绝对路径不应拼到 exe 目录：got %q, want %q", got, abs)
	}

	// 计划任务的 Arguments 里常见带引号的写法
	got, err = requestedPath(base, `"`+abs+`"`)
	if err != nil {
		t.Fatal(err)
	}
	if got != abs {
		t.Errorf("带引号的绝对路径 = %q, want %q", got, abs)
	}
}

func TestRequestedPathRelative(t *testing.T) {
	base := `C:\Apps\PortProxy`
	got, err := requestedPath(base, "data")
	if err != nil {
		t.Fatal(err)
	}
	want := filepath.Join(base, "data")
	if got != want {
		t.Errorf("相对路径 = %q, want %q", got, want)
	}
}

func TestResolveForReadAbsoluteDataDir(t *testing.T) {
	dir := t.TempDir()
	ctx, err := ResolveForRead(dir)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.EqualFold(filepath.Clean(ctx.DataDir), filepath.Clean(dir)) {
		t.Errorf("ResolveForRead(%q) = %q", dir, ctx.DataDir)
	}
}
