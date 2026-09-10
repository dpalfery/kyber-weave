import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Tauri expects a fixed dev-server port so the Rust webview can connect reliably. The
// `@tauri-apps/plugin-*` runtime expects these HMR + strictPort settings to mirror what
// `tauri dev` spawns; tweaking them breaks the IPC bridge on first boot.
const TAURI_DEV_PORT = 1420

export default defineConfig(async () => ({
  plugins: [react()],
  clearScreen: false,
  server: {
    port: TAURI_DEV_PORT,
    strictPort: true,
    host: process.env.TAURI_DEV_HOST || false,
    hmr: process.env.TAURI_DEV_HOST
      ? { protocol: 'ws', host: process.env.TAURI_DEV_HOST, port: 1421 }
      : undefined,
    watch: {
      ignored: ['**/src-tauri/**'],
    },
  },
  envPrefix: ['VITE_', 'TAURI_ENV_*'],
  build: {
    target: process.env.TAURI_ENV_PLATFORM === 'windows' ? 'chrome105' : 'safari13',
    minify: !process.env.TAURI_ENV_DEBUG ? 'esbuild' : false,
    sourcemap: !!process.env.TAURI_ENV_DEBUG,
  },
}))
