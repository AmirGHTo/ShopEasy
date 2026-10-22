const usd = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' })

// The catalog prices are US dollars (the Product API's v2 says so in its `currency` field)
export function formatPrice(amount: number): string {
  return usd.format(amount)
}

// Money is added up in cents: 0.1 + 0.2 is not 0.3 in floating point, and a total must be exact
export function toCents(price: number): number {
  return Math.round(price * 100)
}

export function formatCents(cents: number): string {
  return usd.format(cents / 100)
}
