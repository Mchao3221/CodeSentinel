import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vitest/config'

// https://vite.dev/config/
export default defineConfig({
  plugins: [vue()],
  server: {
    // 开发期把接口请求转发到本地后端
    proxy: {
      '/api': 'http://localhost:5214',
    },
  },
  build: {
    // Element Plus 全量引入后单个依赖包超过默认 500 kB 提示阈值，属预期体积
    chunkSizeWarningLimit: 1200,
  },
  test: {
    // 组件与路由测试运行在浏览器模拟环境中
    environment: 'jsdom',
    globals: true,
    include: ['src/**/*.spec.ts'],
  },
})
