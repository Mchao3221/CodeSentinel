import { defineConfig, devices } from '@playwright/test';

/**
 * Playwright 冒烟测试配置。
 * 测试只驱动真实前端页面，接口由用例内的 page.route 打桩，不依赖后端、数据库与 git。
 */
export default defineConfig({
  // 用例目录与单元测试隔离，避免被 vitest 的 include 规则误收
  testDir: './e2e',
  // 单实例冒烟，失败时重试一次并保留首次重试的 trace
  fullyParallel: false,
  retries: 1,
  workers: 1,
  // 首次失败重试时记录 trace，便于排查动画或时序问题
  timeout: 60_000,
  use: {
    baseURL: 'http://127.0.0.1:4173',
    trace: 'on-first-retry',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  // 复用已运行的开发服务；未运行时由 Playwright 启动 Vite 开发服务
  webServer: {
    command: 'npm run dev -- --host 127.0.0.1 --port 4173 --strictPort',
    url: 'http://127.0.0.1:4173',
    reuseExistingServer: true,
    timeout: 120_000,
  },
});
