import path from 'node:path'
import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// API requests are proxied to the ASP.NET Core backend in development, so the browser uses same-origin URLs.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': path.resolve(__dirname, './src') },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: process.env.VITE_API_PROXY_TARGET ?? 'http://localhost:5080', changeOrigin: true },
      '/health': { target: process.env.VITE_API_PROXY_TARGET ?? 'http://localhost:5080', changeOrigin: true },
    },
  },
})
