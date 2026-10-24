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

// "Oct 23, 2026, 10:00 AM" in the visitor's own time zone; empty when the text is not a date
export function formatDateTime(iso: string): string {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  return new Intl.DateTimeFormat('en-US', { dateStyle: 'medium', timeStyle: 'short' }).format(date)
}
