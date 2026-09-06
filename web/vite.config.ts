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
    //
    // The target is overridable because 5199 is only where `dotnet run` puts
    // it. Checking a UI change against a real INSTALLED instance — which is
    // the only place the service account, the locked data directory and a
    // hospital's own data exist — means pointing at whatever port the
    // installer was given:
    //
    //   HOSPITALPM_API=http://127.0.0.1:5080 npm run dev
    proxy: {
      '/api': process.env.HOSPITALPM_API ?? 'http://127.0.0.1:5199',
      '/health': process.env.HOSPITALPM_API ?? 'http://127.0.0.1:5199',
    },
  },
})
