import { defineConfig, devices } from '@playwright/test'

// Real-browser tests of the storefront, against a running stack: the gateway serves the built app
// and every service behind it is the real one (CI starts them with Docker Compose; see the README,
// "Browser tests"). They are slow and few: the unit tests under src/ cover the details, these prove
// that the pieces work together in a browser, under the gateway's Content-Security-Policy.
export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.e2e.ts',
  // one browser at a time: the tests share the gateway's rate limits and the shop's stock
  workers: 1,
  fullyParallel: false,
  // a flaky test is a bug to find, not to hide
  retries: 0,
  forbidOnly: Boolean(process.env.CI),
  timeout: 120_000,
  expect: { timeout: 15_000 },
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: process.env.SHOP_URL ?? 'http://localhost:5000',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    // a browser that is already on the machine, instead of the one `npx playwright install` fetches
    launchOptions: process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {},
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
})
