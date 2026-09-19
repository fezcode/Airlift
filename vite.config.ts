import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
// Keep Vite's emptyOutDir cleanup away from native binaries and installers in dist.
export default defineConfig({ plugins: [react()], build: { outDir: 'artifacts/web-preview' }, server: { port: 1420, strictPort: true } });
