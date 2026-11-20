// 应用内确认对话框（Promise 风格）：替代 window.confirm。
import { reactive } from 'vue'

export interface ConfirmOptions {
  title: string
  body: string
  confirmLabel?: string
  cancelLabel?: string
  danger?: boolean
}

export const confirmState = reactive<{
  visible: boolean
  opts: ConfirmOptions | null
}>({ visible: false, opts: null })

let resolver: ((v: boolean) => void) | null = null

export function confirmDialog(opts: ConfirmOptions): Promise<boolean> {
  // 串行假设：已有未决确认时先按取消处理
  resolver?.(false)
  confirmState.opts = opts
  confirmState.visible = true
  return new Promise(resolve => { resolver = resolve })
}

export function resolveConfirm(v: boolean): void {
  confirmState.visible = false
  resolver?.(v)
  resolver = null
}
