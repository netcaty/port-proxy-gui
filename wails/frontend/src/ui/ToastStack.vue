<script setup lang="ts">
// toast 堆栈：右下角（状态栏上方），点击即关，自动过期由 toast.ts 控制。
import { toastState, dismissToast } from './toast'
import Icon from './Icon.vue'

const ICONS: Record<string, string> = { success: 'check', error: 'x', warn: 'warning', info: 'info' }
</script>

<template>
  <Teleport to="body">
    <div class="toast-stack" role="status" aria-live="polite">
      <div v-for="t in toastState.items" :key="t.id" class="toast" :class="t.kind"
           @click="dismissToast(t.id)">
        <Icon :name="ICONS[t.kind] ?? 'info'" :size="14" />
        <span>{{ t.text }}</span>
      </div>
    </div>
  </Teleport>
</template>
