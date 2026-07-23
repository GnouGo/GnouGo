import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
  },
  server: {
    port: 5501,
    proxy: {
      '/api': { target: 'http://localhost:5500', changeOrigin: true },
      '/health': { target: 'http://localhost:5500', changeOrigin: true },
    },
  },
})
