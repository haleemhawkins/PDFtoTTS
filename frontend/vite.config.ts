/// <reference types="vitest/config" />
import { defineConfig, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'
import { createRequire } from 'node:module'
import { cpSync, existsSync, statSync, createReadStream } from 'node:fs'
import { join, dirname, extname } from 'node:path'

const require = createRequire(import.meta.url)
const pdfjsRoot = dirname(require.resolve('pdfjs-dist/package.json'))

// pdf.js runtime assets it loads on demand, none of which the bundler can see:
//   - cmaps/          CID font character maps (CJK / CID-keyed fonts)
//   - standard_fonts/ metrics for the 14 non-embedded standard fonts
//   - wasm/           JBIG2 + OpenJPEG decoders for scanned page images
// Without wasm/ in particular, scanned PDFs (JBIG2 page images + an invisible
// OCR text layer) render BLANK while the text still extracts — so the word
// highlight floats on an empty page. Serve them at /pdfjs/* in dev and copy
// them into the build output. (usePdfDocument points pdf.js here.)
const PDFJS_DIRS = ['cmaps', 'standard_fonts', 'wasm'] as const

function pdfjsAssets(): Plugin {
  return {
    name: 'pdfjs-assets',
    configureServer(server) {
      server.middlewares.use('/pdfjs', (req, res, next) => {
        const rel = decodeURIComponent((req.url ?? '').split('?')[0]).replace(/^\/+/, '')
        const file = join(pdfjsRoot, rel)
        if (!file.startsWith(pdfjsRoot) || !existsSync(file) || !statSync(file).isFile()) return next()
        if (extname(file) === '.wasm') res.setHeader('Content-Type', 'application/wasm')
        createReadStream(file).pipe(res)
      })
    },
    writeBundle(options) {
      const out = options.dir ?? 'dist'
      for (const d of PDFJS_DIRS) cpSync(join(pdfjsRoot, d), join(out, 'pdfjs', d), { recursive: true })
    },
  }
}

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), pdfjsAssets()],
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
