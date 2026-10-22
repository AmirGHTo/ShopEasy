import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vitest/config'

// The gateway the dev server forwards the API and the sign-in to (`npm run dev` against the Compose
// stack: http://localhost:5000). In production the gateway serves this app itself, so there is one
// origin and nothing to configure.
const gateway = process.env.VITE_GATEWAY_URL ?? 'http://localhost:5000'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      '/api': gateway,
      '/account': gateway,
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    coverage: {
      provider: 'v8',
      include: ['src/**/*.{ts,tsx}'],
      exclude: ['src/**/*.test.{ts,tsx}', 'src/test/**', 'src/main.tsx'],
      // a floor, not a goal: a change that leaves code untested fails here instead of going unnoticed
      thresholds: { statements: 90, branches: 85, functions: 90, lines: 90 },
    },
  },
})
