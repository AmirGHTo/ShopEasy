import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { fingerprintOf, forgetAttempt, keyForAttempt, newIdempotencyKey } from './checkoutAttempt'

const id = (n: number) => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`

beforeEach(() => {
  sessionStorage.clear()
})
afterEach(() => {
  vi.restoreAllMocks()
})

describe('newIdempotencyKey', () => {
  it('is a version 4 UUID, which the Order API accepts', () => {
    const key = newIdempotencyKey()

    expect(key).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect(key).toMatch(/^[A-Za-z0-9._~:-]{1,128}$/)
  })

  it('is different every time', () => {
    expect(new Set(Array.from({ length: 200 }, () => newIdempotencyKey())).size).toBe(200)
  })
})

describe('fingerprintOf', () => {
  const lines = [
    { productId: id(5), quantity: 2 },
    { productId: id(3), quantity: 1 },
  ]

  it('does not depend on the order of the lines, or on spaces around the note', () => {
    expect(fingerprintOf([...lines].reverse(), '  hello ')).toBe(fingerprintOf(lines, 'hello'))
  })

  it.each([
    ['another quantity', [{ productId: id(5), quantity: 3 }, { productId: id(3), quantity: 1 }], ''],
    ['another product', [{ productId: id(6), quantity: 2 }, { productId: id(3), quantity: 1 }], ''],
    ['one line fewer', [{ productId: id(5), quantity: 2 }], ''],
    ['another note', lines, 'new note'],
  ])('differs for %s', (_what, otherLines, notes) => {
    expect(fingerprintOf(otherLines, notes)).not.toBe(fingerprintOf(lines, ''))
  })
})

describe('keyForAttempt', () => {
  it('gives the same key for the same order, however often it is asked', () => {
    const first = keyForAttempt('order-a')

    expect(keyForAttempt('order-a')).toBe(first)
    expect(keyForAttempt('order-a')).toBe(first)
  })

  it('gives another key when the order changed, and keeps only the latest', () => {
    const first = keyForAttempt('order-a')
    const second = keyForAttempt('order-b')

    expect(second).not.toBe(first)
    expect(keyForAttempt('order-b')).toBe(second)
    expect(keyForAttempt('order-a')).not.toBe(first) // the first attempt is gone: a new order
  })

  it('keeps the key over a reload of the page (it is in sessionStorage)', () => {
    const key = keyForAttempt('order-a')

    // a reload loses every variable; the storage stays
    expect(sessionStorage.getItem('shopeasy.checkout.v1')).toContain(key)
    expect(keyForAttempt('order-a')).toBe(key)
  })

  it('starts again after the order was placed', () => {
    const key = keyForAttempt('order-a')

    forgetAttempt()

    expect(keyForAttempt('order-a')).not.toBe(key)
  })

  it.each(['{"broken', 'null', '"text"', '{"fingerprint":"order-a","key":"has spaces and /"}', '{"fingerprint":1,"key":2}'])(
    'does not trust damaged storage (%s)',
    (stored) => {
      sessionStorage.setItem('shopeasy.checkout.v1', stored)

      const key = keyForAttempt('order-a')

      expect(key).toMatch(/^[A-Za-z0-9._~:-]{1,128}$/)
      expect(key).not.toContain(' ')
    },
  )

  it('still gives a key when the browser will not store anything', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked')
    })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => {
      throw new Error('blocked')
    })

    expect(keyForAttempt('order-a')).toMatch(/^[0-9a-f-]{36}$/)
    expect(() => { forgetAttempt() }).not.toThrow()
  })
})
