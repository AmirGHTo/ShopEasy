import { Link } from 'react-router'
import { formatCents } from '../../lib/format'
import { MAX_QUANTITY, itemCount } from './cart'
import CartRow from './CartRow'
import { useCartPricing } from './useCartPricing'

export default function CartPage() {
  const { cart, rows, buyable, subtotalCents, someGone } = useCartPricing()

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
          <span>{formatCents(subtotalCents)}</span>
        </p>
        {someGone && <p className="text-sm text-slate-600">Products that are no longer available are not counted.</p>}
        <p className="text-sm text-slate-600">
          Prices are confirmed when you place the order. At most {MAX_QUANTITY} of a product.
        </p>
        {buyable.length > 0 && (
          <Link
            to="/checkout"
            className="mt-3 rounded-lg bg-indigo-600 px-5 py-2.5 font-medium text-white hover:bg-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2"
          >
            Checkout
          </Link>
        )}
      </div>
    </div>
  )
}
