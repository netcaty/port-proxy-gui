<script setup lang="ts">
// 日志查看器（对应 C# UI/LogViewerForm.cs 的简化版）
import { onMounted, ref } from 'vue'
import * as AppService from '../../bindings/portproxymanager/appservice'
import type { LogFile } from '../../bindings/portproxymanager/models'

const emit = defineEmits<{ (e: 'close'): void }>()

const files = ref<LogFile[]>([])
const current = ref('')
const content = ref('加载中…')

async function open(name: string) {
  current.value = name
  content.value = '加载中…'
  try {
    content.value = await AppService.ReadLog(name)
  } catch (e) {
    content.value = '读取失败：' + String(e)
  }
}

onMounted(async () => {
  files.value = (await AppService.ListLogs()) as LogFile[]
  if (files.value.length > 0) {
    await open(files.value[0].name)
  } else {
    content.value = '（还没有日志文件）'
  }
})
</script>

<template>
  <Teleport to="body">
    <div class="modal-mask" @click.self="emit('close')">
      <div class="modal" style="min-width: 720px">
        <div class="modal-header">操作日志</div>
        <div class="modal-body">
          <div style="display: flex; gap: 8px; margin-bottom: 10px; flex-wrap: wrap">
            <button v-for="f in files.slice(0, 7)" :key="f.name"
                    :style="{ fontWeight: f.name === current ? 600 : 400 }"
                    @click="open(f.name)">{{ f.name }}</button>
            <span v-if="files.length === 0" class="dim">无日志文件</span>
          </div>
          <div class="log-view" style="max-height: 50vh">{{ content }}</div>
        </div>
        <div class="modal-footer">
          <button @click="emit('close')">关闭</button>
        </div>
      </div>
    </div>
  </Teleport>
</template>
