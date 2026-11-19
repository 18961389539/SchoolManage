import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import Components from 'unplugin-vue-components/vite'
import { ElementPlusResolver } from 'unplugin-vue-components/resolvers'

export default defineConfig({
  plugins: [
    vue(),
    Components({
      dts: 'src/components.d.ts',
      resolvers: [ElementPlusResolver({ importStyle: 'css', directives: true })]
    })
  ],
  server: { port: 5173, proxy: { '/api': { target: 'http://127.0.0.1:5080', changeOrigin: false, ws: true } } }
})
