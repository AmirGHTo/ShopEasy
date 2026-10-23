import { useQueries } from '@tanstack/react-query'
import { Link } from 'react-router'
import { formatCents, toCents } from '../../lib/format'
import { productQuery } from '../catalog/queries'
import { MAX_QUANTITY, itemCount, type CartLine } from './cart'
import CartRow from './CartRow'
import { useCart } from './useCart'

export default function CartPage() {
  const cart = useCart()
  const results = useQueries({ queries: cart.lines.map((line) => productQuery(line.productId)) })

  if (cart.lines.length === 0) {
    return (
      <div className="flex flex-col items-start gap-3">
        <title>Your cart · ShopEasy</title>
        <h1 className="text-3xl font-semibold tracking-tight">Your cart</h1>
        <p className="text-slate-600">Your cart is empty.</p>
        <Link to="/products" className="font-medium text-indigo-600 hover:text-indigo-500">
          Browse the products
        </Link>
      </div>
    )
  }

  // what can be bought: the product is known and still sold
  const rows = cart.lines.map((line, index) => ({ line, result: results[index] }))
  const buyable: { line: CartLine; price: number }[] = []
  for (const { line, result } of rows) {
    if (result?.data?.isActive) buyable.push({ line, price: result.data.price })
  }
  const subtotal = buyable.reduce((sum, { line, price }) => sum + toCents(price) * line.quantity, 0)
  const someGone = rows.some(({ result }) => result?.isSuccess && !result.data?.isActive)

  return (
    <div className="flex flex-col gap-6">
      <title>Your cart · ShopEasy</title>
      <h1 className="text-3xl font-semibold tracking-tight">Your cart</h1>

      <ul className="flex flex-col divide-y divide-slate-200 rounded-xl border border-slate-200 bg-white">
        {rows.map(({ line, result }) =>
          result ? (
            <CartRow
              key={line.productId}
              line={line}
              result={result}
              onQuantity={(quantity) => { cart.setQuantity(line.productId, quantity) }}
              onRemove={() => { cart.remove(line.productId) }}
            />
          ) : null,
        )}
      </ul>

      <div className="flex flex-col items-end gap-1">
        <p className="text-lg font-semibold text-slate-900">
          Subtotal ({String(itemCount(buyable.map(({ line }) => line)))} items):{' '}
          <span>{formatCents(subtotal)}</span>
        </p>
        {someGone && <p className="text-sm text-slate-600">Products that are no longer available are not counted.</p>}
        <p className="text-sm text-slate-600">
          Prices are confirmed when you place the order. At most {MAX_QUANTITY} of a product.
        </p>
      </div>
    </div>
  )
}
