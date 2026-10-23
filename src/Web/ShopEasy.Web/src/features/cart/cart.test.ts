import { describe, expect, it } from 'vitest'
import {
  addToCart,
  canAdd,
  EMPTY_CART,
  itemCount,
  MAX_LINES,
  MAX_QUANTITY,
  parseStoredCart,
  removeFromCart,
  setQuantity,
  type Cart,
} from './cart'

const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`
const line = (n: number, quantity = 1) => ({ productId: id(n), quantity })

describe('addToCart', () => {
  it('starts a line with one', () => {
    expect(addToCart(EMPTY_CART, id(1))).toEqual([line(1)])
  })

  it('adds one more to a line that is there, and leaves the others', () => {
    expect(addToCart([line(1, 2), line(2)], id(1))).toEqual([line(1, 3), line(2)])
  })

  it('does not go past the most the Order API takes for one product', () => {
    const full: Cart = [line(1, MAX_QUANTITY)]

    expect(addToCart(full, id(1))).toBe(full)
    expect(canAdd(full, id(1))).toBe(false)
  })

  it('does not go past the most lines the Order API takes', () => {
    const full: Cart = Array.from({ length: MAX_LINES }, (_, i) => line(i + 1))

    expect(addToCart(full, id(999))).toBe(full)
    expect(canAdd(full, id(999))).toBe(false)
    expect(canAdd(full, id(1))).toBe(true) // one more of what is there is fine
  })

  it('does not change the cart it was given', () => {
    const cart: Cart = [line(1)]

    addToCart(cart, id(1))

    expect(cart).toEqual([line(1)])
  })
})

describe('setQuantity', () => {
  it('sets it', () => {
    expect(setQuantity([line(1), line(2)], id(2), 7)).toEqual([line(1), line(2, 7)])
  })

  it.each([
    [0, 1],
    [-5, 1],
    [1000, MAX_QUANTITY],
    [2.9, 2],
  ])('turns %s into %s', (asked, kept) => {
    expect(setQuantity([line(1)], id(1), asked)).toEqual([line(1, kept)])
  })

  it.each([Number.NaN, Number.POSITIVE_INFINITY])('ignores %s', (asked) => {
    const cart: Cart = [line(1, 3)]

    expect(setQuantity(cart, id(1), asked)).toBe(cart)
  })

  it('leaves the cart alone for a product that is not in it, or a quantity it has already', () => {
    const cart: Cart = [line(1, 3)]

    expect(setQuantity(cart, id(2), 5)).toBe(cart)
    expect(setQuantity(cart, id(1), 3)).toBe(cart)
  })
})

describe('removeFromCart', () => {
  it('removes the line', () => {
    expect(removeFromCart([line(1), line(2)], id(1))).toEqual([line(2)])
  })

  it('leaves the cart alone for a product that is not in it', () => {
    const cart: Cart = [line(1)]

    expect(removeFromCart(cart, id(2))).toBe(cart)
  })
})

describe('itemCount', () => {
  it('counts the pieces, not the lines', () => {
    expect(itemCount([line(1, 2), line(2, 3)])).toBe(5)
    expect(itemCount(EMPTY_CART)).toBe(0)
  })
})

describe('parseStoredCart', () => {
  it('reads a cart it wrote', () => {
    expect(parseStoredCart(JSON.stringify([line(1, 2), line(2)]))).toEqual([line(1, 2), line(2)])
  })

  it('has an empty cart for nothing stored', () => {
    expect(parseStoredCart(null)).toBe(EMPTY_CART)
  })

  it.each([
    ['text that is not JSON', 'not json {'],
    ['something other than a list', '{"productId":"x"}'],
    ['a product id that is not a UUID', JSON.stringify([{ productId: '../admin', quantity: 1 }])],
    ['a quantity of 0', JSON.stringify([line(1, 0)])],
    ['a quantity past the limit', JSON.stringify([line(1, MAX_QUANTITY + 1)])],
    ['a quantity that is not whole', JSON.stringify([line(1, 1.5)])],
    ['a quantity that is text', JSON.stringify([{ productId: id(1), quantity: '2' }])],
    ['a product twice', JSON.stringify([line(1), line(1)])],
    ['more lines than an order can have', JSON.stringify(Array.from({ length: MAX_LINES + 1 }, (_, i) => line(i + 1)))],
  ])('has an empty cart for %s', (_what, stored) => {
    expect(parseStoredCart(stored)).toBe(EMPTY_CART)
  })

  it('drops fields it does not know (a price put there by hand)', () => {
    const stored = JSON.stringify([{ ...line(1), price: 0.01 }])

    expect(parseStoredCart(stored)).toEqual([line(1)])
  })
})
