import { createApp } from 'vue'
import { ElMessage } from 'element-plus'
import 'element-plus/es/components/message/style/css'
import 'element-plus/es/components/message-box/style/css'
import './styles.css'
import App from './App.vue'

const app = createApp(App)
// A render-time exception must not leave the operator with a blank page and no explanation.
app.config.errorHandler = (error, _instance, info) => {
  console.error(`[ui] ${info}`, error)
  ElMessage.error('界面出现异常，请刷新页面后重试')
}
app.mount('#app')
