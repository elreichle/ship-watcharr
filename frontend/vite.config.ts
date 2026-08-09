import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Dev-only: the production build is served by the ASP.NET Core backend
      // itself, same-origin, so no proxy is needed there.
      '/api': {
        target: process.env.BACKEND_URL ?? 'http://localhost:5110',
        changeOrigin: true,
      },
    },
  },
})
