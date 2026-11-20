<script setup lang="ts">
// 确认对话框（配合 confirm.ts 的 Promise）：Esc 取消，Enter 确认。
import { nextTick, onMounted, onUnmounted, ref } from 'vue'
import { confirmState, resolveConfirm } from './confirm'
import Icon from './Icon.vue'

const confirmBtn = ref<HTMLButtonElement | null>(null)

function onKey(e: KeyboardEvent) {
  if (!confirmState.visible) return
  if (e.key === 'Escape') { e.stopPropagation(); resolveConfirm(false) }
  else if (e.key === 'Enter') { e.stopPropagation(); resolveConfirm(true) }
}

onMounted(async () => {
  window.addEventListener('keydown', onKey, true)
  await nextTick()
  confirmBtn.value?.focus()
})
onUnmounted(() => window.removeEventListener('keydown', onKey, true))
</script>

<template>
  <Teleport to="body">
    <div v-if="confirmState.visible && confirmState.opts" class="modal-mask"
         @click.self="resolveConfirm(false)">
      <div class="modal confirm-modal" role="alertdialog" :aria-label="confirmState.opts.title">
        <div class="confirm-head">
          <Icon :name="confirmState.opts.danger ? 'warning' : 'info'" :size="16" />
          <span>{{ confirmState.opts.title }}</span>
        </div>
        <div class="modal-body confirm-body">{{ confirmState.opts.body }}</div>
        <div class="modal-footer">
          <button class="btn" @click="resolveConfirm(false)">{{ confirmState.opts.cancelLabel ?? '取消' }}</button>
          <button ref="confirmBtn" class="btn" :class="confirmState.opts.danger ? 'danger' : 'primary'"
                  @click="resolveConfirm(true)">{{ confirmState.opts.confirmLabel ?? '确定' }}</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
