import { randomUUID } from 'node:crypto'
import { expect, test as base, type ConsoleMessage } from '@playwright/test'

// What a developer would see in the browser's console, and the shop must keep empty: a script error,
// a request that failed, a Content-Security-Policy violation. Every test collects them and fails if
// there is one, whatever else it checks.
const test = base.extend<{ problems: string[] }>({
  problems: [
    async ({ page }, use) => {
      const problems: string[] = []

      // a policy violation is also an error in the console; the event names the directive and what was blocked
      await page.addInitScript(() => {
        document.addEventListener('securitypolicyviolation', (event) => {
          console.error(
            `Content-Security-Policy: ${event.violatedDirective} blocked ${event.blockedURI} ` +
              `(${event.sourceFile}:${String(event.lineNumber)}) ${event.sample}`,
          )
        })
      })

      page.on('console', (message) => {
        if (message.type() === 'error' && !isTheSignedOutAnswer(message) && !isAMissingFavicon(message)) {
          problems.push(`console: ${message.text()} [${message.location().url}]`)
        }
      })
      page.on('pageerror', (error) => problems.push(`script error: ${error.message}`))
      page.on('requestfailed', (request) => {
        // a request given up on because the page moved on (a sign-in redirect) is not a failure
        if (request.failure()?.errorText !== 'net::ERR_ABORTED') problems.push(`request failed: ${request.url()}`)
      })

      await use(problems)
      expect(problems, 'the browser console and network, which must stay clean').toEqual([])
    },
    { auto: true },
  ],
})

// A visitor who is not signed in is asked who they are, and the answer 401 is part of how the app
// learns that; the browser logs it in the console like any failed request.
const isTheSignedOutAnswer = (message: ConsoleMessage) =>
  message.location().url.endsWith('/account/me') && message.text().includes('401')

// The sign-in and sign-up pages belong to Identity, which has no icon of its own to give a browser
// that asks for /favicon.ico. The shop's pages name theirs (a link to /favicon.svg), so it is not asked for.
const isAMissingFavicon = (message: ConsoleMessage) =>
  message.location().url.endsWith('/favicon.ico') && message.text().includes('404')

const headphones = { id: '00000000-0000-0000-0000-000000000001', name: 'Wireless Headphones' }

test('a customer signs up, buys something, sees the order confirmed, and cancels it', async ({ page }) => {
  const email = `e2e-browser-${randomUUID()}@example.com`
  const password = `Aa1-${randomUUID().replaceAll('-', '')}`

  await test.step('the shop opens on the products', async () => {
    await page.goto('/')
    await expect(page).toHaveURL(/\/products$/)
    await expect(page.getByRole('heading', { level: 1, name: 'Products' })).toBeVisible()
    await expect(page.getByRole('link', { name: headphones.name })).toBeVisible()
  })

  await test.step('a product goes into the cart', async () => {
    await page.getByRole('link', { name: headphones.name }).click()
    await expect(page.getByRole('heading', { level: 1, name: headphones.name })).toBeVisible()

    await page.getByRole('button', { name: 'Add to cart' }).click()

    await expect(page.getByText('Added to your cart.')).toBeVisible()
    await expect(page.getByRole('link', { name: 'Cart, 1 item' })).toBeVisible()
  })

  await test.step('checking out asks the visitor to sign in, and the cart is kept', async () => {
    await page.getByRole('link', { name: 'Cart, 1 item' }).click()
    await expect(page.getByRole('heading', { level: 1, name: 'Your cart' })).toBeVisible()
    await page.getByRole('link', { name: 'Checkout' }).click()

    await expect(page.getByRole('heading', { level: 1, name: 'Checkout' })).toBeVisible()
    await page.getByRole('link', { name: 'Sign in' }).last().click()
  })

  await test.step('they create an account and come back to the checkout', async () => {
    await page.getByRole('link', { name: 'Create an account' }).click()
    await page.getByLabel('First name').fill('Browser')
    await page.getByLabel('Last name').fill('Shopper')
    await page.getByLabel('Email address').fill(email)
    await page.getByLabel('Password', { exact: true }).fill(password)
    await page.getByLabel('Confirm password').fill(password)
    await page.getByRole('button', { name: 'Create account' }).click()

    await expect(page).toHaveURL(/\/checkout$/)
    await expect(page.getByRole('heading', { level: 1, name: 'Checkout' })).toBeVisible()
    await expect(page.getByText(headphones.name)).toBeVisible()
  })

  await test.step('they place the order and watch it being confirmed', async () => {
    await page.getByRole('button', { name: 'Place order' }).click()

    await expect(page).toHaveURL(/\/orders\/[0-9a-f-]{36}$/)
    // the stock is reserved and the payment taken in the background: the page looks again by itself
    await expect(page.getByText('Your order is confirmed.')).toBeVisible({ timeout: 60_000 })
    await expect(page.getByText('Succeeded')).toBeVisible()
    await expect(page.getByRole('link', { name: 'Cart, 0 items' })).toBeVisible() // the cart was emptied
  })

  await test.step('the order is in their list', async () => {
    await page.getByRole('link', { name: 'Orders' }).click()

    await expect(page.getByRole('heading', { level: 1, name: 'Your orders' })).toBeVisible()
    const order = page.getByRole('listitem').filter({ hasText: headphones.name })
    await expect(order.getByText('Confirmed')).toBeVisible()
  })

  await test.step('they cancel it, and the money goes back', async () => {
    await page.getByRole('listitem').filter({ hasText: headphones.name }).getByRole('link').click()
    await expect(page.getByText('Your order is confirmed.')).toBeVisible()

    await page.getByRole('button', { name: 'Cancel order' }).click()
    await page.getByRole('button', { name: 'Yes, cancel the order' }).click()

    await expect(page.getByText('Your order was cancelled.')).toBeVisible({ timeout: 60_000 })
    await expect(page.getByText('Refunded')).toBeVisible()
  })

  await test.step('they sign out', async () => {
    await page.getByRole('link', { name: 'Sign out' }).click()

    // back in the shop, as a visitor: signing out goes through Identity and returns
    await expect(page).toHaveURL(/\/products$/)
    await expect(page.getByRole('link', { name: 'Sign in' })).toBeVisible()
  })
})

test.describe('on a phone', () => {
  test.use({ viewport: { width: 375, height: 667 } })

  for (const [name, path, heading] of [
    ['the product list', '/products', 'Products'],
    ['a product', `/products/${headphones.id}`, headphones.name],
    ['an empty cart', '/cart', 'Your cart'],
    ['the checkout', '/checkout', 'Checkout'],
  ] as const) {
    test(`${name} fits the screen without sideways scrolling`, async ({ page }) => {
      await page.goto(path)
      await expect(page.getByRole('heading', { level: 1, name: heading })).toBeVisible()

      const { content, screen } = await page.evaluate(() => ({
        content: document.documentElement.scrollWidth,
        screen: window.innerWidth,
      }))

      expect(content).toBeLessThanOrEqual(screen)
    })
  }
})
