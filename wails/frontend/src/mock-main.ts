// 仅本地 UI 预览入口：先装上 mock-fetch，再挂载 App。
// 真实入口是 main.ts；mocked.html 不会被 wails3 build 打进产物。
import './mock-api'
import { createApp } from 'vue'
import App from './App.vue'

createApp(App).mount('#app')
