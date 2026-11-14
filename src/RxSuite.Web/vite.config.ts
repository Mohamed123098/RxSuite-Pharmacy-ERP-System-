import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // Calls to /api go to the ASP.NET Core API (see launchSettings.json in the Api project).
    proxy: {
      '/api': { target: 'https://localhost:7080', changeOrigin: true, secure: false },
    },
  },
})
