import { useState } from 'react'
import { Link } from 'react-router'
import { MAX_LINES, MAX_QUANTITY } from './cart'
import { useCart } from './useCart'

// "Add to cart" for one product. It says what happened (a polite live region, so that a screen
// reader hears it too) and why it cannot add when the cart's limits are reached.
export default function AddToCart({ productId }: { productId: string }) {
  const cart = useCart()
  const [added, setAdded] = useState(false)

  const inCart = cart.lines.find((line) => line.productId === productId)
  const possible = cart.canAdd(productId)

  return (
    <div className="flex flex-col items-start gap-2">
      <button
        type="button"
        disabled={!possible}
        onClick={() => {
          cart.add(productId)
          setAdded(true)
        }}
        className="rounded-lg bg-indigo-600 px-5 py-2.5 font-medium text-white hover:bg-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
      >
        Add to cart
      </button>

      <p role="status" className="text-sm text-slate-700">
        {!possible && inCart && `You already have the most you can order of this product (${String(MAX_QUANTITY)}).`}
        {!possible && !inCart && `Your cart is full (${String(MAX_LINES)} products).`}
        {possible && added && inCart && (
          <>
            {inCart.quantity === 1 ? 'Added to your cart. ' : `In your cart: ${String(inCart.quantity)}. `}
            <Link to="/cart" className="font-medium text-indigo-600 hover:text-indigo-500">
              View cart
            </Link>
          </>
        )}
      </p>
    </div>
  )
}
