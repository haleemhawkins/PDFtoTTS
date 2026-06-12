/// <reference types="vitest/config" />
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Bind all interfaces so the dev server is reachable over the Tailscale VPN
    // (e.g. http://acearchlinux:5173), not just localhost.
    host: true,
    // Recent Vite rejects unknown Host headers; allow the Tailscale MagicDNS name.
    allowedHosts: ['acearchlinux', 'acearchlinux.tail2751b.ts.net'],
    // Dev proxy so the SPA can call the API/hub on the same origin. These targets
    // run on THIS machine, so VPN clients reach them through the proxy.
    proxy: {
      '/api': 'http://localhost:8080',
      '/hubs': { target: 'http://localhost:8080', ws: true },
    },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    // Unit tests live under src/. The e2e/ specs use Playwright's runner, not
    // vitest, so keep them out of the vitest sweep.
    include: ['src/**/*.{test,spec}.{ts,tsx}'],
  },
})
