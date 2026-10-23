import { z } from 'zod'

// The limits of the Order API (OrderLimits in the Order service). The cart keeps to them, so that
// an order made from it is never refused for being too big.
export const MAX_LINES = 50
export const MAX_QUANTITY = 100

// A cart line is only which product and how many. No name, no price: those come from the catalog
// when the cart is shown, and the order itself is priced by the server, so a stale or edited
// number in the browser can never change what is charged.
export const cartLineSchema = z.object({
  productId: z.guid(), // a .NET Guid, see productSchema
  quantity: z.int().min(1).max(MAX_QUANTITY),
})

export type CartLine = z.infer<typeof cartLineSchema>
export type Cart = readonly CartLine[]

export const EMPTY_CART: Cart = []

// What is read back from the browser's storage is not to be trusted: another tab, an older version
// of the shop or the user themself may have written it. It is the whole cart or nothing.
const storedCartSchema = z
  .array(cartLineSchema)
  .max(MAX_LINES)
  .refine((lines) => new Set(lines.map((line) => line.productId)).size === lines.length, 'a product twice')

export function parseStoredCart(stored: string | null): Cart {
  if (stored === null) return EMPTY_CART
  try {
    const parsed = storedCartSchema.safeParse(JSON.parse(stored))
    return parsed.success ? parsed.data : EMPTY_CART
  } catch {
    return EMPTY_CART // not even JSON
  }
}

// All of these return the same cart object when nothing changes, so that callers can tell.

export function addToCart(cart: Cart, productId: string): Cart {
  const line = cart.find((l) => l.productId === productId)
  if (!line) return cart.length >= MAX_LINES ? cart : [...cart, { productId, quantity: 1 }]
  if (line.quantity >= MAX_QUANTITY) return cart
  return cart.map((l) => (l === line ? { ...l, quantity: l.quantity + 1 } : l))
}

export function canAdd(cart: Cart, productId: string): boolean {
  return addToCart(cart, productId) !== cart
}

export function setQuantity(cart: Cart, productId: string, quantity: number): Cart {
  if (!Number.isFinite(quantity)) return cart
  const line = cart.find((l) => l.productId === productId)
  const wanted = Math.min(MAX_QUANTITY, Math.max(1, Math.trunc(quantity)))
  if (!line || line.quantity === wanted) return cart
  return cart.map((l) => (l === line ? { ...l, quantity: wanted } : l))
}

export function removeFromCart(cart: Cart, productId: string): Cart {
  const rest = cart.filter((l) => l.productId !== productId)
  return rest.length === cart.length ? cart : rest
}

export function itemCount(cart: Cart): number {
  return cart.reduce((sum, line) => sum + line.quantity, 0)
}
