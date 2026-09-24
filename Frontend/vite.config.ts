import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/sse': {
        target: 'http://localhost:5100',
        changeOrigin: true,
      },
      '/api': {
        target: 'http://localhost:5100',
        changeOrigin: true,
      },
    },
  },
})
