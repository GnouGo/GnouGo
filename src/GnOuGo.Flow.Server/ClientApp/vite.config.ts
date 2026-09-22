import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    rolldownOptions: {
      output: {
        codeSplitting: {
          groups: [{ name: 'react-vendor', test: /node_modules\/(react|react-dom|scheduler)\// }]
        }
      }
    }
  },
  server: {
    port: 5301,
    proxy: {
      '/api': {
        target: 'http://localhost:5300',
        changeOrigin: true
      }
    }
  }
})
