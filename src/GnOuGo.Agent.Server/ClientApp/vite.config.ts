import { defineConfig } from 'vite';
import path from 'node:path';

// Build to a stable, offline-friendly output:
//   wwwroot/ui/app.js
//   wwwroot/ui/app.css
//   wwwroot/ui/chunks/*
export default defineConfig({
  build: {
    outDir: path.resolve(__dirname, '../wwwroot/ui'),
    emptyOutDir: true,
    sourcemap: false,
    cssCodeSplit: false,
    rollupOptions: {
      input: path.resolve(__dirname, 'src/main.ts'),
      output: {
        entryFileNames: 'app.js',
        chunkFileNames: 'chunks/[name].js',
        assetFileNames: (asset) => {
          if (asset.name && asset.name.endsWith('.css')) return 'app.css';
          return 'assets/[name][extname]';
        },
      },
    },
  },
});