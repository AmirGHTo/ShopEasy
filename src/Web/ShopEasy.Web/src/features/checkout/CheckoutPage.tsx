import { useMutation, useQueryClient } from '@tanstack/react-query'
import { useState, type SyntheticEvent } from 'react'
import { Link, useNavigate } from 'react-router'
import { formatCents, toCents } from '../../lib/format'
import SignInPrompt from '../../components/SignInPrompt'
import { sessionQueryKey, useSession } from '../../session/useSession'
import { useCartPricing } from '../cart/useCartPricing'
import { NOTES_MAX_LENGTH, placeOrder } from '../orders/orders'
import { fingerprintOf, forgetAttempt, keyForAttempt } from './checkoutAttempt'
import { describeOrderProblem } from './orderProblem'
import OrderProblemNotice from './OrderProblemNotice'

const linkClass = 'font-medium text-indigo-600 hover:text-indigo-500'

// Checkout is for a signed-in customer. The cart is kept while they sign in (it lives in the browser),
// and the sign-in brings them back here.
export default function CheckoutPage() {
  const session = useSession()

  return (
    <div className="flex flex-col gap-6">
      <title>Checkout · ShopEasy</title>
      <h1 className="text-3xl font-semibold tracking-tight">Checkout</h1>

      {session.isPending && <div role="status" aria-label="Loading" className="h-40 animate-pulse rounded-xl bg-slate-200" />}

      {session.isError && <p className="text-slate-600">Your account is unavailable. Try again in a moment.</p>}

      {session.data === null && <SignInPrompt message="Sign in to place your order. Your cart is kept." returnTo="/checkout" />}

      {session.data && <CheckoutForm />}
    </div>
  )
}

function CheckoutForm() {
  const { cart, buyable, subtotalCents, someGone, pending, failed } = useCartPricing()
  const [notes, setNotes] = useState('')
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const place = useMutation({
    // The key is the one for this order: the same while the cart and the note stay the same, so that
    // a second press after a lost answer cannot order twice (see checkoutAttempt)
    mutationFn: () =>
      placeOrder({
        lines: cart.lines,
        notes,
        idempotencyKey: keyForAttempt(fingerprintOf(cart.lines, notes)),
      }),
    onSuccess: (order) => {
      forgetAttempt()
      void navigate(`/orders/${order.id}`, { replace: true })
      cart.clear()
    },
    onError: (error) => {
      // the session ended: look again, and the page asks the customer to sign in
      if (describeOrderProblem(error).kind === 'signed-out') {
        void queryClient.invalidateQueries({ queryKey: sessionQueryKey })
      }
    },
  })

  if (cart.lines.length === 0) {
    return (
      <div className="flex flex-col items-start gap-3">
        <p className="text-slate-600">Your cart is empty.</p>
        <Link to="/products" className={linkClass}>
          Browse the products
        </Link>
      </div>
    )
  }

  // the order is not placed until every line is something that can be bought
  const blocker = pending
    ? 'Checking your cart…'
    : failed
      ? 'We could not check every product in your cart.'
      : someGone || buyable.length === 0
        ? 'Some products in your cart are no longer available. Remove them to continue.'
        : null

  const submit = (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (blocker === null && !place.isPending) place.mutate()
  }

  return (
    <form onSubmit={submit} aria-busy={place.isPending} className="flex flex-col gap-6">
      <ul className="flex flex-col divide-y divide-slate-200 rounded-xl border border-slate-200 bg-white">
        {buyable.map(({ line, product }) => (
          <li key={line.productId} className="flex items-center justify-between gap-4 p-4">
            <span className="text-slate-900">
              {product.name} <span className="text-slate-500">× {line.quantity}</span>
            </span>
            <span className="font-medium text-slate-900">{formatCents(toCents(product.price) * line.quantity)}</span>
          </li>
        ))}
      </ul>

      <div className="flex flex-col gap-1">
        <label htmlFor="notes" className="text-sm font-medium text-slate-700">
          Notes for the order (optional)
        </label>
        <textarea
          id="notes"
          name="notes"
          rows={3}
          maxLength={NOTES_MAX_LENGTH}
          value={notes}
          onChange={(event) => { setNotes(event.target.value) }}
          className="rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500"
        />
        <span className="self-end text-xs text-slate-500">
          {notes.length} / {NOTES_MAX_LENGTH}
        </span>
      </div>

      <div className="flex flex-col items-end gap-1">
        <p className="text-lg font-semibold text-slate-900">
          Estimated total: <span>{formatCents(subtotalCents)}</span>
        </p>
        <p className="text-sm text-slate-600">The shop confirms the prices when the order is placed.</p>
      </div>

      {place.isError && <OrderProblemNotice problem={describeOrderProblem(place.error)} />}

      {blocker !== null && (
        <p role="status" className="text-sm text-slate-700">
          {blocker}{' '}
          {!pending && (
            <Link to="/cart" className={linkClass}>
              Back to the cart
            </Link>
          )}
        </p>
      )}

      <div className="flex items-center justify-between">
        <Link to="/cart" className={linkClass}>
          ← Back to the cart
        </Link>
        <button
          type="submit"
          disabled={blocker !== null || place.isPending}
          className="rounded-lg bg-indigo-600 px-6 py-2.5 font-medium text-white hover:bg-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50"
        >
          {place.isPending ? 'Placing your order…' : 'Place order'}
        </button>
      </div>
    </form>
  )
}
