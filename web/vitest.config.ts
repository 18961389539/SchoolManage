import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vitest/config'

export default defineConfig({
  // The route table imports page components, so the runner needs the SFC compiler even
  // though the assertions never render them.
  plugins: [vue()],
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
    reporters: ['default']
  }
})
