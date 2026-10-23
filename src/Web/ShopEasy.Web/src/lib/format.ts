const usd = new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' })

// The catalog prices are US dollars (the Product API's v2 says so in its `currency` field)
export function formatPrice(amount: number): string {
  return usd.format(amount)
}
