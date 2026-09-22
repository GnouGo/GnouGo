import { defineConfig } from 'vite';
import path from 'node:path';

// Build to a stable, offline-friendly output:
//   wwwroot/ui/app.js
//   wwwroot/ui/app.css
//   wwwroot/ui/chunks/*
export default defineConfig({
  base: '/ui/',
  plugins: [{
    name: 'diagram-chunk-budgets',
    generateBundle(_options, bundle) {
      for (const chunk of Object.values(bundle)) {
        if (chunk.type !== 'chunk') continue;
        // Mermaid 12 ships ELK as one prebuilt module (about 1.46 MB).
        // It stays lazy-loaded; retain the 700 kB budget for every other chunk.
        const modules = Object.keys(chunk.modules).map(id => id.replaceAll('\\', '/'));
        const isElk = modules.length === 2
          && modules.some(id => id.endsWith('/elkjs/lib/elk.bundled.js'))
          && modules.some(id => /\/mermaid\/dist\/chunks\/mermaid\.core\/elk-[^/]+\.mjs$/.test(id));
        const budget = isElk ? 1500 : 700;
        if (Buffer.byteLength(chunk.code) > budget * 1000) {
          this.error(`${chunk.fileName} exceeds its ${budget} kB chunk budget`);
        }
      }
    },
  }],
  build: {
    outDir: path.resolve(import.meta.dirname, '../wwwroot/ui'),
    emptyOutDir: true,
    sourcemap: false,
    cssCodeSplit: false,
    // The plugin above enforces the smaller limit except for the ELK module.
    chunkSizeWarningLimit: 1500,
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
