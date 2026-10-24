import { describe, expect, it } from 'vitest'
import { formatCents, formatDateTime, formatPrice, toCents } from './format'

describe('formatPrice', () => {
  it.each([
    [299.99, '$299.99'],
    [1299, '$1,299.00'],
    [0.5, '$0.50'],
  ])('writes %s as %s', (amount, text) => {
    expect(formatPrice(amount)).toBe(text)
  })
})

describe('toCents and formatCents', () => {
  it('add up exactly where floating point does not', () => {
    expect(0.1 + 0.2).not.toBe(0.3)
    expect(formatCents(toCents(0.1) + toCents(0.2))).toBe('$0.30')
  })

  it('round a price to the cent', () => {
    expect(toCents(299.99)).toBe(29999)
    expect(toCents(19.999)).toBe(2000)
  })
})

describe('formatDateTime', () => {
  it('writes a date and a time', () => {
    expect(formatDateTime('2026-10-23T10:00:00Z')).toMatch(/^Oct 2[34], 2026, \d{1,2}:\d{2}\s?[AP]M$/)
  })

  it('writes nothing for what is not a date', () => {
    expect(formatDateTime('')).toBe('')
    expect(formatDateTime('yesterday')).toBe('')
  })
})
