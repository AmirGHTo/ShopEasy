import { describe, expect, it } from 'vitest'
import { formatPrice } from './format'

describe('formatPrice', () => {
  it.each([
    [299.99, '$299.99'],
    [1299, '$1,299.00'],
    [0.5, '$0.50'],
  ])('writes %s as %s', (amount, text) => {
    expect(formatPrice(amount)).toBe(text)
  })
})
