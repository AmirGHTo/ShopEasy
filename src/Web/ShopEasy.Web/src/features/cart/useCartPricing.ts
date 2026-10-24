import { useQueries } from '@tanstack/react-query'
import { toCents } from '../../lib/format'
import type { Product } from '../catalog/catalog'
import { productQuery } from '../catalog/queries'
import type { CartLine } from './cart'
import { useCart } from './useCart'

// The cart's lines with the products they stand for, read from the catalog (the cart itself keeps
// no names or prices). The prices are an estimate: the order is priced by the server.
export function useCartPricing() {
  const cart = useCart()
  const results = useQueries({ queries: cart.lines.map((line) => productQuery(line.productId)) })

  const rows = cart.lines.map((line, index) => ({ line, result: results[index] }))

  // what can be bought: the product is known and still sold
  const buyable: { line: CartLine; product: Product }[] = []
  for (const { line, result } of rows) {
    if (result?.data?.isActive) buyable.push({ line, product: result.data })
  }

  // added up in cents, see toCents
  const subtotalCents = buyable.reduce((sum, { line, product }) => sum + toCents(product.price) * line.quantity, 0)

  return {
    cart,
    rows,
    buyable,
    subtotalCents,
    // some line is a product that no longer exists or is no longer sold
    someGone: rows.some(({ result }) => result?.isSuccess && !result.data?.isActive),
    pending: results.some((result) => result.isPending),
    failed: results.some((result) => result.isError),
  }
}
