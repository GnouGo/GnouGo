import { defineConfig } from 'vite';
import path from 'node:path';

// Build to a stable, offline-friendly output:
//   wwwroot/ui/app.js
//   wwwroot/ui/app.css
//   wwwroot/ui/chunks/*
export default defineConfig({
  build: {
    outDir: path.resolve(import.meta.dirname, '../wwwroot/ui'),
    emptyOutDir: true,
    sourcemap: false,
    cssCodeSplit: false,
    // Mermaid's optional architecture grammar is a single upstream module and
    // cannot be split further. It is loaded only when a diagram is rendered.
    chunkSizeWarningLimit: 700,
    rolldownOptions: {
      input: path.resolve(import.meta.dirname, 'src/main.ts'),
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
