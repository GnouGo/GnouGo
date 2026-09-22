import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  base: '/ui/',
  build: { outDir: '../wwwroot/ui', emptyOutDir: true },
  server: { host: '127.0.0.1', proxy: {
    '/api': { target: 'http://127.0.0.1:5087', changeOrigin: true, configure(proxy) {
      proxy.on('proxyReq', request => {
        request.setHeader('origin', 'http://127.0.0.1:5087')
        request.setHeader('sec-fetch-site', 'same-origin')
      })
    } }
  } }
})
