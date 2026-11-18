// Package appicon 程序化生成与 C# UI/AppIcon.cs 一致的图标：
// 深蓝圆角方块（#1F4B78）上白色 ⇄ 双向转发箭头（上线向右、下线向左）。
// 避免二进制资源文件的思路与 C# 版相同；超采样抗锯齿。
package appicon

import (
	"bytes"
	"image"
	"image/color"
	"image/png"
	"math"
)

const (
	baseSize      = 32.0             // C# 绘制坐标系
	bgR, bgG, bgB = 0x1F, 0x4B, 0x78 // 深蓝
)

// Generate 生成 size×size 的图标（RGBA）。
func Generate(size int) *image.RGBA {
	scale := float64(size) / baseSize
	super := 4 // 4×4 超采样抗锯齿
	s2 := size * super
	canvas := image.NewRGBA(image.Rect(0, 0, s2, s2))
	sScale := scale * float64(super)

	// 深蓝圆角方块：rect(1.5, 1.5, size-1.5, size-1.5)，圆角 7
	x0, y0 := 1.5*sScale, 1.5*sScale
	x1, y1 := float64(s2)-1.5*sScale, float64(s2)-1.5*sScale
	r := 7 * sScale
	lineW := math.Max(1.6, 2.1*sScale) / 2 // 半宽

	topY := 12 * sScale
	bottomY := 20 * sScale

	for py := 0; py < s2; py++ {
		for px := 0; px < s2; px++ {
			fx, fy := float64(px)+0.5, float64(py)+0.5
			switch {
			case inRoundedRect(fx, fy, x0, y0, x1, y1, r):
				// 优先判背景；前景元素在上面覆盖
				if inCapsule(fx, fy, 7*sScale, topY, 20*sScale, topY, lineW) ||
					inCapsule(fx, fy, 12*sScale, bottomY, 25*sScale, bottomY, lineW) ||
					inTriangle(fx, fy, 20*sScale, topY-4.2*sScale, 20*sScale, topY+4.2*sScale, 26*sScale, topY) ||
					inTriangle(fx, fy, 12*sScale, bottomY-4.2*sScale, 12*sScale, bottomY+4.2*sScale, 6*sScale, bottomY) {
					canvas.Set(px, py, color.RGBA{255, 255, 255, 255})
				} else {
					canvas.Set(px, py, color.RGBA{bgR, bgG, bgB, 255})
				}
			default:
				canvas.Set(px, py, color.RGBA{0, 0, 0, 0}) // 透明底
			}
		}
	}

	// 超采样下采样到目标尺寸
	out := image.NewRGBA(image.Rect(0, 0, size, size))
	step := super * super
	for y := 0; y < size; y++ {
		for x := 0; x < size; x++ {
			var r, g, b, a uint32
			for dy := 0; dy < super; dy++ {
				for dx := 0; dx < super; dx++ {
					c := canvas.At(x*super+dx, y*super+dy).(color.RGBA)
					r += uint32(c.R)
					g += uint32(c.G)
					b += uint32(c.B)
					a += uint32(c.A)
				}
			}
			out.Set(x, y, color.RGBA{
				R: uint8(r / uint32(step)),
				G: uint8(g / uint32(step)),
				B: uint8(b / uint32(step)),
				A: uint8(a / uint32(step)),
			})
		}
	}
	return out
}

// TrayIconPNG 托盘图标（PNG 字节，32×32 与 C# 版一致）。
func TrayIconPNG() []byte {
	var buf bytes.Buffer
	if err := png.Encode(&buf, Generate(32)); err != nil {
		return nil
	}
	return buf.Bytes()
}

// ICOBytes 生成内嵌 PNG 的 ICO（Vista+ 支持），用于 exe 资源图标。
func ICOBytes(size int) ([]byte, error) {
	img := Generate(size)
	var pngBuf bytes.Buffer
	if err := png.Encode(&pngBuf, img); err != nil {
		return nil, err
	}
	pngData := pngBuf.Bytes()

	// ICONDIR(6) + ICONDIRENTRY(16) + PNG 数据；256 用 0 表示
	entryW, entryH := byte(size), byte(size)
	if size >= 256 {
		entryW, entryH = 0, 0
	}
	out := make([]byte, 0, 22+len(pngData))
	out = append(out, 0, 0, 1, 0, 1, 0)     // ICONDIR: reserved=0, type=icon, count=1
	out = append(out, entryW, entryH, 0, 0) // entry: w, h, colors, reserved
	out = append(out, 1, 0)                 // planes = 1 (LE16)
	out = append(out, 32, 0)                // bitCount = 32 (LE16)
	out = append(out,
		byte(len(pngData)), byte(len(pngData)>>8),
		byte(len(pngData)>>16), byte(len(pngData)>>24))
	out = append(out, 22, 0, 0, 0) // offset
	out = append(out, pngData...)
	return out, nil
}

// inRoundedRect 圆角矩形包含判断（圆角用四分之一圆近似，与 AddArc 一致）。
func inRoundedRect(px, py, x0, y0, x1, y1, r float64) bool {
	if px < x0 || px > x1 || py < y0 || py > y1 {
		return false
	}
	// 四个角区域：判点到对应圆心距离
	cx, cy := 0.0, 0.0
	switch {
	case px < x0+r && py < y0+r:
		cx, cy = x0+r, y0+r
	case px > x1-r && py < y0+r:
		cx, cy = x1-r, y0+r
	case px < x0+r && py > y1-r:
		cx, cy = x0+r, y1-r
	case px > x1-r && py > y1-r:
		cx, cy = x1-r, y1-r
	default:
		return true
	}
	dx, dy := px-cx, py-cy
	return dx*dx+dy*dy <= r*r
}

// inCapsule 圆头线段：点到线段距离 ≤ 半径。
func inCapsule(px, py, x1, y1, x2, y2, radius float64) bool {
	dx, dy := x2-x1, y2-y1
	lengthSq := dx*dx + dy*dy
	t := 0.0
	if lengthSq > 0 {
		t = ((px-x1)*dx + (py-y1)*dy) / lengthSq
		if t < 0 {
			t = 0
		} else if t > 1 {
			t = 1
		}
	}
	cx, cy := x1+t*dx, y1+t*dy
	ex, ey := px-cx, py-cy
	return ex*ex+ey*ey <= radius*radius
}

// inTriangle 三角形包含判断（重心/边函数，允许微小外扩以贴近 GDI FillPolygon）。
func inTriangle(px, py, ax, ay, bx, by, cx, cy float64) bool {
	d1 := sign(px, py, ax, ay, bx, by)
	d2 := sign(px, py, bx, by, cx, cy)
	d3 := sign(px, py, cx, cy, ax, ay)
	hasNeg := d1 < 0 || d2 < 0 || d3 < 0
	hasPos := d1 > 0 || d2 > 0 || d3 > 0
	return !(hasNeg && hasPos)
}

func sign(px, py, ax, ay, bx, by float64) float64 {
	return (px-bx)*(ay-by) - (ax-bx)*(py-by)
}
