import { defineConfig, mergeConfig } from 'vitest/config'
import viteConfig from './vite.config.ts'

// Unit/component tests run in jsdom. Playwright end-to-end specs live in e2e/ and are excluded here.
export default mergeConfig(viteConfig, defineConfig({
  // Tests only: lib/ml-features.test.ts reads the server's ML feature catalogue to keep the explanations in sync.
  server: { fs: { allow: ['.', '../../src/PortfolioOptimizer.MachineLearning/Features'] } },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    restoreMocks: true,
  },
}))
