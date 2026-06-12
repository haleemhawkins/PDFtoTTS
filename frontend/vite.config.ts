/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Dev proxy so the SPA can call the API/hub on the same origin.
    proxy: {
      '/api': 'http://localhost:8080',
      '/hubs': { target: 'http://localhost:8080', ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
  },
})
