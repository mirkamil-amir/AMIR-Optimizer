import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// API requests are proxied to the ASP.NET Core backend in development, so the browser uses same-origin URLs.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(import.meta.dirname, './src') },
    // Keep "node_modules/…" paths when node_modules is a symlink (to node_modules.nosync, see docs/testing.md) so
    // dependencies are still recognised and pre-bundled.
    preserveSymlinks: true,
  },
  server: {
    port: 5173,
    // node_modules may be a symlink to node_modules.nosync (keeps iCloud from offloading it); never watch either.
    watch: { ignored: ['**/node_modules.nosync/**'] },
    proxy: {
      '/api': { target: process.env.VITE_API_PROXY_TARGET ?? 'http://localhost:5080', changeOrigin: true },
      '/health': { target: process.env.VITE_API_PROXY_TARGET ?? 'http://localhost:5080', changeOrigin: true },
    },
  },
})
