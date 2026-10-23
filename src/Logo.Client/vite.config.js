import { defineConfig } from 'vite'

// Dev:   `npm run dev` startet Fable (watch) + Vite; /ws wird an den ASP.NET-Server (Port 5030) weitergeleitet.
// Build: `npm run build` schreibt das fertige Frontend nach ../Logo.Server/wwwroot.
export default defineConfig({
  build: {
    outDir: '../Logo.Server/wwwroot',
    emptyOutDir: true
  },
  server: {
    port: 5173,
    proxy: {
      '/ws': { target: 'ws://localhost:5030', ws: true }
    }
  }
})
