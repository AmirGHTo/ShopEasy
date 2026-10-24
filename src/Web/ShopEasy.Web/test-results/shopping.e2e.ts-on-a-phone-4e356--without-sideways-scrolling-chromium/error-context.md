# Instructions

- Following Playwright test failed.
- Explain why, be concise, respect Playwright best practices.
- Provide a snippet of code with the fix, if possible.

# Test info

- Name: shopping.e2e.ts >> on a phone >> the product list fits the screen without sideways scrolling
- Location: e2e/shopping.e2e.ts:141:5

# Error details

```
Error: the browser console and network, which must stay clean

expect(received).toEqual(expected) // deep equality

- Expected  - 1
+ Received  + 3

- Array []
+ Array [
+   "console: Content-Security-Policy: script-src blocked eval (http://localhost:5000/assets/schemas-BI3-7bV2.js:1)  []",
+ ]
```

# Page snapshot

```yaml
- generic [ref=e2]:
  - link "Skip to content" [ref=e3] [cursor=pointer]:
    - /url: "#content"
  - banner [ref=e4]:
    - generic [ref=e5]:
      - link "ShopEasy" [ref=e6] [cursor=pointer]:
        - /url: /
      - navigation "Account" [ref=e7]:
        - link "Sign in" [ref=e8] [cursor=pointer]:
          - /url: /account/login?returnUrl=%2Fproducts
        - link "Cart, 0 items" [ref=e9] [cursor=pointer]:
          - /url: /cart
          - text: Cart0
  - main [ref=e10]:
    - generic [ref=e11]:
      - heading "Products" [level=1] [ref=e12]
      - generic [ref=e13]:
        - search [ref=e14]:
          - generic [ref=e15]:
            - generic [ref=e16]: Search products
            - searchbox "Search products" [ref=e17]
          - button "Search" [ref=e18]
        - generic [ref=e19]:
          - generic [ref=e20]: Category
          - combobox "Category" [ref=e21]:
            - option "All categories" [selected]
            - option "Electronics"
            - option "Furniture"
      - list [ref=e22]:
        - listitem [ref=e23]:
          - generic [aria-hidden] [ref=e24]: "4"
          - generic [ref=e25]:
            - paragraph [ref=e26]: Electronics
            - heading [level=2] [ref=e27]:
              - link "4K Webcam" [ref=e28] [cursor=pointer]:
                - /url: /products/00000000-0000-0000-0000-000000000005
            - paragraph [ref=e29]: 1080p/60fps with built-in ring light
          - paragraph [ref=e30]: $89.99
        - listitem [ref=e31]:
          - generic [aria-hidden] [ref=e32]: E
          - generic [ref=e33]:
            - paragraph [ref=e34]: Furniture
            - heading [level=2] [ref=e35]:
              - link "Ergonomic Office Chair" [ref=e36] [cursor=pointer]:
                - /url: /products/00000000-0000-0000-0000-000000000004
            - paragraph [ref=e37]: Lumbar support, adjustable armrests
          - paragraph [ref=e38]: $449.99
        - listitem [ref=e39]:
          - generic [aria-hidden] [ref=e40]: M
          - generic [ref=e41]:
            - paragraph [ref=e42]: Electronics
            - heading [level=2] [ref=e43]:
              - link "Mechanical Keyboard" [ref=e44] [cursor=pointer]:
                - /url: /products/00000000-0000-0000-0000-000000000002
            - paragraph [ref=e45]: TKL layout with Cherry MX switches
          - paragraph [ref=e46]: $149.99
        - listitem [ref=e47]:
          - generic [aria-hidden] [ref=e48]: S
          - generic [ref=e49]:
            - paragraph [ref=e50]: Furniture
            - heading [level=2] [ref=e51]:
              - link "Standing Desk" [ref=e52] [cursor=pointer]:
                - /url: /products/00000000-0000-0000-0000-000000000003
            - paragraph [ref=e53]: Motorized sit/stand desk, 140x70cm
          - paragraph [ref=e54]: $599.99
        - listitem [ref=e55]:
          - generic [aria-hidden] [ref=e56]: W
          - generic [ref=e57]:
            - paragraph [ref=e58]: Electronics
            - heading [level=2] [ref=e59]:
              - link "Wireless Headphones" [ref=e60] [cursor=pointer]:
                - /url: /products/00000000-0000-0000-0000-000000000001
            - paragraph [ref=e61]: Premium noise-cancelling headphones
          - paragraph [ref=e62]: $299.99
```

# Test source

```ts
  1   | import { randomUUID } from 'node:crypto'
  2   | import { expect, test as base, type ConsoleMessage } from '@playwright/test'
  3   | 
  4   | // What a developer would see in the browser's console, and the shop must keep empty: a script error,
  5   | // a request that failed, a Content-Security-Policy violation. Every test collects them and fails if
  6   | // there is one, whatever else it checks.
  7   | const test = base.extend<{ problems: string[] }>({
  8   |   problems: [
  9   |     async ({ page }, use) => {
  10  |       const problems: string[] = []
  11  | 
  12  |       // a policy violation is also an error in the console; the event names the directive and what was blocked
  13  |       await page.addInitScript(() => {
  14  |         document.addEventListener('securitypolicyviolation', (event) => {
  15  |           console.error(
  16  |             `Content-Security-Policy: ${event.violatedDirective} blocked ${event.blockedURI} ` +
  17  |               `(${event.sourceFile}:${String(event.lineNumber)}) ${event.sample}`,
  18  |           )
  19  |         })
  20  |       })
  21  | 
  22  |       page.on('console', (message) => {
  23  |         if (message.type() === 'error' && !isTheSignedOutAnswer(message) && !isAMissingFavicon(message)) {
  24  |           problems.push(`console: ${message.text()} [${message.location().url}]`)
  25  |         }
  26  |       })
  27  |       page.on('pageerror', (error) => problems.push(`script error: ${error.message}`))
  28  |       page.on('requestfailed', (request) => {
  29  |         // a request given up on because the page moved on (a sign-in redirect) is not a failure
  30  |         if (request.failure()?.errorText !== 'net::ERR_ABORTED') problems.push(`request failed: ${request.url()}`)
  31  |       })
  32  | 
  33  |       await use(problems)
> 34  |       expect(problems, 'the browser console and network, which must stay clean').toEqual([])
      |                                                                                  ^ Error: the browser console and network, which must stay clean
  35  |     },
  36  |     { auto: true },
  37  |   ],
  38  | })
  39  | 
  40  | // A visitor who is not signed in is asked who they are, and the answer 401 is part of how the app
  41  | // learns that; the browser logs it in the console like any failed request.
  42  | const isTheSignedOutAnswer = (message: ConsoleMessage) =>
  43  |   message.location().url.endsWith('/account/me') && message.text().includes('401')
  44  | 
  45  | // The sign-in and sign-up pages belong to Identity, which has no icon of its own to give a browser
  46  | // that asks for /favicon.ico. The shop's pages name theirs (a link to /favicon.svg), so it is not asked for.
  47  | const isAMissingFavicon = (message: ConsoleMessage) =>
  48  |   message.location().url.endsWith('/favicon.ico') && message.text().includes('404')
  49  | 
  50  | const headphones = { id: '00000000-0000-0000-0000-000000000001', name: 'Wireless Headphones' }
  51  | 
  52  | test('a customer signs up, buys something, sees the order confirmed, and cancels it', async ({ page }) => {
  53  |   const email = `e2e-browser-${randomUUID()}@example.com`
  54  |   const password = `Aa1-${randomUUID().replaceAll('-', '')}`
  55  | 
  56  |   await test.step('the shop opens on the products', async () => {
  57  |     await page.goto('/')
  58  |     await expect(page).toHaveURL(/\/products$/)
  59  |     await expect(page.getByRole('heading', { level: 1, name: 'Products' })).toBeVisible()
  60  |     await expect(page.getByRole('link', { name: headphones.name })).toBeVisible()
  61  |   })
  62  | 
  63  |   await test.step('a product goes into the cart', async () => {
  64  |     await page.getByRole('link', { name: headphones.name }).click()
  65  |     await expect(page.getByRole('heading', { level: 1, name: headphones.name })).toBeVisible()
  66  | 
  67  |     await page.getByRole('button', { name: 'Add to cart' }).click()
  68  | 
  69  |     await expect(page.getByText('Added to your cart.')).toBeVisible()
  70  |     await expect(page.getByRole('link', { name: 'Cart, 1 item' })).toBeVisible()
  71  |   })
  72  | 
  73  |   await test.step('checking out asks the visitor to sign in, and the cart is kept', async () => {
  74  |     await page.getByRole('link', { name: 'Cart, 1 item' }).click()
  75  |     await expect(page.getByRole('heading', { level: 1, name: 'Your cart' })).toBeVisible()
  76  |     await page.getByRole('link', { name: 'Checkout' }).click()
  77  | 
  78  |     await expect(page.getByRole('heading', { level: 1, name: 'Checkout' })).toBeVisible()
  79  |     await page.getByRole('link', { name: 'Sign in' }).last().click()
  80  |   })
  81  | 
  82  |   await test.step('they create an account and come back to the checkout', async () => {
  83  |     await page.getByRole('link', { name: 'Create an account' }).click()
  84  |     await page.getByLabel('First name').fill('Browser')
  85  |     await page.getByLabel('Last name').fill('Shopper')
  86  |     await page.getByLabel('Email address').fill(email)
  87  |     await page.getByLabel('Password', { exact: true }).fill(password)
  88  |     await page.getByLabel('Confirm password').fill(password)
  89  |     await page.getByRole('button', { name: 'Create account' }).click()
  90  | 
  91  |     await expect(page).toHaveURL(/\/checkout$/)
  92  |     await expect(page.getByRole('heading', { level: 1, name: 'Checkout' })).toBeVisible()
  93  |     await expect(page.getByText(headphones.name)).toBeVisible()
  94  |   })
  95  | 
  96  |   await test.step('they place the order and watch it being confirmed', async () => {
  97  |     await page.getByRole('button', { name: 'Place order' }).click()
  98  | 
  99  |     await expect(page).toHaveURL(/\/orders\/[0-9a-f-]{36}$/)
  100 |     // the stock is reserved and the payment taken in the background: the page looks again by itself
  101 |     await expect(page.getByText('Your order is confirmed.')).toBeVisible({ timeout: 60_000 })
  102 |     await expect(page.getByText('Succeeded')).toBeVisible()
  103 |     await expect(page.getByRole('link', { name: 'Cart, 0 items' })).toBeVisible() // the cart was emptied
  104 |   })
  105 | 
  106 |   await test.step('the order is in their list', async () => {
  107 |     await page.getByRole('link', { name: 'Orders' }).click()
  108 | 
  109 |     await expect(page.getByRole('heading', { level: 1, name: 'Your orders' })).toBeVisible()
  110 |     const order = page.getByRole('listitem').filter({ hasText: headphones.name })
  111 |     await expect(order.getByText('Confirmed')).toBeVisible()
  112 |   })
  113 | 
  114 |   await test.step('they cancel it, and the money goes back', async () => {
  115 |     await page.getByRole('listitem').filter({ hasText: headphones.name }).getByRole('link').click()
  116 |     await expect(page.getByText('Your order is confirmed.')).toBeVisible()
  117 | 
  118 |     await page.getByRole('button', { name: 'Cancel order' }).click()
  119 |     await page.getByRole('button', { name: 'Yes, cancel the order' }).click()
  120 | 
  121 |     await expect(page.getByText('Your order was cancelled.')).toBeVisible({ timeout: 60_000 })
  122 |     await expect(page.getByText('Refunded')).toBeVisible()
  123 |   })
  124 | 
  125 |   await test.step('they sign out', async () => {
  126 |     await page.getByRole('link', { name: 'Sign out' }).click()
  127 | 
  128 |     await expect(page.getByRole('link', { name: 'Sign in' })).toBeVisible()
  129 |   })
  130 | })
  131 | 
  132 | test.describe('on a phone', () => {
  133 |   test.use({ viewport: { width: 375, height: 667 } })
  134 | 
```