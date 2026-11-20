// 应用内 toast：替代 window.alert（WebView2 原生对话框带 URL 横幅、阻塞渲染、样式不可控）。
import { reactive } from 'vue'

export type ToastKind = 'success' | 'error' | 'warn' | 'info'

export interface ToastItem {
  id: number
  kind: ToastKind
  text: string
}

export const toastState = reactive<{ items: ToastItem[] }>({ items: [] })

let seq = 0

export function dismissToast(id: number): void {
  const i = toastState.items.findIndex(t => t.id === id)
  if (i >= 0) toastState.items.splice(i, 1)
}

function push(kind: ToastKind, text: string): void {
  const id = ++seq
  toastState.items.push({ id, kind, text })
  // 最多同时 4 条，超出丢最旧的
  if (toastState.items.length > 4) toastState.items.shift()
  const ttl = kind === 'error' ? 8000 : 4500
  setTimeout(() => dismissToast(id), ttl)
}

export const toast = {
  success: (text: string) => push('success', text),
  error: (text: string) => push('error', text),
  warn: (text: string) => push('warn', text),
  info: (text: string) => push('info', text),
}
