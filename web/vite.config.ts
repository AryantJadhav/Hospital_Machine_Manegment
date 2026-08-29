import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The SPA is not served separately. It builds into the API's wwwroot so the
// published self-contained binary carries the UI with it — one binary, per
// the packaging constraint in CLAUDE.md.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: '../src/HospitalPm.Api/wwwroot',
    emptyOutDir: true,
  },
  server: {
    // `npm run dev` proxies API calls to the locally running .NET service,
    // so the dev loop matches production paths without CORS config.
    proxy: {
      '/api': 'http://127.0.0.1:5199',
      '/health': 'http://127.0.0.1:5199',
    },
  },
})
