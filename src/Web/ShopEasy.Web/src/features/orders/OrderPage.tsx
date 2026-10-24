import { Link, useLocation, useParams } from 'react-router'
import { ApiError } from '../../api/http'
import { formatDateTime } from '../../lib/format'
import NotFoundPage from '../../pages/NotFoundPage'
import SignInPrompt from '../../components/SignInPrompt'
import { isSettled, type OrderSummary } from './orders'
import { MAX_POLLS, POLL_MS, useOrder } from './queries'

const linkClass = 'font-medium text-indigo-600 hover:text-indigo-500'

// What each status means to the customer. A status the shop adds later is shown as it is.
const STATUS_TEXT: Record<string, { title: string; tone: 'good' | 'bad' | 'neutral' }> = {
  Confirmed: { title: 'Your order is confirmed.', tone: 'good' },
  Shipped: { title: 'Your order has been shipped.', tone: 'good' },
  Delivered: { title: 'Your order has been delivered.', tone: 'good' },
  Cancelled: { title: 'Your order was cancelled.', tone: 'bad' },
  PaymentFailed: { title: 'The payment failed, so your order was not completed.', tone: 'bad' },
}

const TONE_CLASS = {
  good: 'border-green-200 bg-green-50 text-green-900',
  bad: 'border-red-200 bg-red-50 text-red-900',
  neutral: 'border-slate-200 bg-white text-slate-800',
} as const

interface OrderPageProps {
  // how often and how many times a Pending order is looked at again (tests make it quick)
  pollMs?: number
  maxPolls?: number
}

// An order after it was placed. It starts as Pending and moves on by itself (stock is reserved, the
// payment taken), so the page keeps looking until it settles.
export default function OrderPage({ pollMs = POLL_MS, maxPolls = MAX_POLLS }: OrderPageProps) {
  const { id = '' } = useParams()
  const { pathname } = useLocation()
  const order = useOrder(id, { pollMs, maxPolls })

  return (
    <div className="flex flex-col gap-6">
      <title>Your order · ShopEasy</title>

      {order.isPending && <div role="status" aria-label="Loading your order" className="h-48 animate-pulse rounded-xl bg-slate-200" />}

      {order.isError &&
        (order.error instanceof ApiError && order.error.status === 401 ? (
          <>
            <h1 className="text-3xl font-semibold tracking-tight">Your order</h1>
            <SignInPrompt message="Sign in to see your order." returnTo={pathname} />
          </>
        ) : (
          <div role="alert" className="flex flex-col items-start gap-3 rounded-xl border border-red-200 bg-red-50 p-4">
            <p className="text-red-800">We could not load your order. It has not been lost.</p>
            <button
              type="button"
              onClick={() => void order.refetch()}
              className="rounded-lg bg-white px-3 py-1.5 text-sm font-medium text-red-800 ring-1 ring-red-300 hover:bg-red-100"
            >
              Try again
            </button>
          </div>
        ))}

      {order.data === null && <NotFoundPage message="There is no such order." />}

      {order.data && (
        <OrderDetails
          order={order.data}
          // still Pending after the last look: say so, and let the customer look again
          gaveUp={!isSettled(order.data.statusName) && order.looks >= maxPolls}
          onRefresh={() => void order.refetch()}
        />
      )}
    </div>
  )
}

function OrderDetails({ order, gaveUp, onRefresh }: { order: OrderSummary; gaveUp: boolean; onRefresh: () => void }) {
  const status = STATUS_TEXT[order.statusName] ?? { title: `Status: ${order.statusName}`, tone: 'neutral' as const }
  const pending = order.statusName === 'Pending'

  return (
    <>
      <h1 className="text-3xl font-semibold tracking-tight">Your order</h1>

      <div
        role="status"
        aria-live="polite"
        aria-busy={pending && !gaveUp}
        className={`flex flex-col gap-2 rounded-xl border p-4 ${TONE_CLASS[pending ? 'neutral' : status.tone]}`}
      >
        {pending ? (
          gaveUp ? (
            <>
              <p className="font-medium">This is taking longer than usual.</p>
              <p className="text-sm">Your order is safe. Check back in a few minutes.</p>
              <button
                type="button"
                onClick={onRefresh}
                className="self-start rounded-lg bg-white px-3 py-1.5 text-sm font-medium text-slate-800 ring-1 ring-slate-300 hover:bg-slate-50"
              >
                Refresh
              </button>
            </>
          ) : (
            <>
              <p className="font-medium">We are processing your order.</p>
              <p className="text-sm">Reserving the stock and taking the payment. This page updates by itself.</p>
            </>
          )
        ) : (
          <>
            <p className="font-medium">{status.title}</p>
            {order.failureReason && <p className="text-sm">Reason: {order.failureReason}</p>}
          </>
        )}
      </div>

      <dl className="grid grid-cols-[max-content_1fr] gap-x-6 gap-y-2 rounded-xl border border-slate-200 bg-white p-4 text-sm">
        <dt className="text-slate-500">Order</dt>
        <dd className="font-mono text-slate-900">{order.orderId}</dd>
        <dt className="text-slate-500">Placed</dt>
        <dd className="text-slate-900">{formatDateTime(order.placedAt)}</dd>
        <dt className="text-slate-500">Items</dt>
        <dd className="text-slate-900">{order.itemsSummary}</dd>
        <dt className="text-slate-500">Total</dt>
        <dd className="font-semibold text-slate-900">{order.amountDisplay}</dd>
        {order.paymentStatus && (
          <>
            <dt className="text-slate-500">Payment</dt>
            <dd className="text-slate-900">{order.paymentStatus}</dd>
          </>
        )}
      </dl>

      <Link to="/products" className={linkClass}>
        Continue shopping
      </Link>
    </>
  )
}
