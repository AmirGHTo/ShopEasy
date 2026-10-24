import { useState } from 'react'
import { canCancel, describeCancelProblem } from './cancel'
import { useCancelOrder } from './queries'

const buttonClass =
  'rounded-lg px-4 py-2 text-sm font-medium focus:outline-none focus:ring-2 focus:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-50'

interface CancelOrderProps {
  orderId: string
  statusName: string
  pollMs: number
  maxPolls: number
}

// "Cancel order" for an order that can still be cancelled, with a question first (it cannot be undone:
// the stock goes back and a payment is refunded). A browser's confirm() box is not used: it cannot be
// styled, announced properly or tested.
export default function CancelOrder({ orderId, statusName, pollMs, maxPolls }: CancelOrderProps) {
  const { cancel, cancelling, gaveUp, refresh } = useCancelOrder(orderId, { pollMs, maxPolls })
  const [asking, setAsking] = useState(false)

  if (cancelling) {
    return (
      <div role="status" aria-live="polite" className="flex flex-col gap-2 rounded-xl border border-slate-200 bg-white p-4">
        {gaveUp ? (
          <>
            <p className="font-medium text-slate-900">Your cancellation is taking longer than usual.</p>
            <p className="text-sm text-slate-600">It was accepted. The stock is being released and any payment refunded. Check back in a few minutes.</p>
            <button type="button" onClick={refresh} className={`${buttonClass} self-start bg-white text-slate-800 ring-1 ring-slate-300 hover:bg-slate-50`}>
              Refresh
            </button>
          </>
        ) : (
          <>
            <p className="font-medium text-slate-900">We are cancelling your order.</p>
            <p className="text-sm text-slate-600">Releasing the stock and refunding any payment. This page updates by itself.</p>
          </>
        )}
      </div>
    )
  }

  if (!canCancel(statusName)) return null

  const problem = cancel.isError ? describeCancelProblem(cancel.error) : null

  return (
    <div className="flex flex-col gap-3">
      {problem && (
        <div role="alert" className="rounded-xl border border-red-200 bg-red-50 p-4 text-sm text-red-900">
          <p>{problem.title}</p>
          {problem.retryAfterSeconds !== undefined && <p>Please wait {problem.retryAfterSeconds} seconds, then try again.</p>}
        </div>
      )}

      {asking ? (
        <div role="group" aria-labelledby="cancel-question" className="flex flex-col gap-3 rounded-xl border border-slate-200 bg-white p-4">
          <p id="cancel-question" className="font-medium text-slate-900">
            Cancel this order?
          </p>
          <p className="text-sm text-slate-600">The stock is released and any payment is refunded. This cannot be undone.</p>
          <div className="flex gap-3">
            <button
              type="button"
              disabled={cancel.isPending}
              onClick={() => { cancel.mutate() }}
              className={`${buttonClass} bg-red-600 text-white hover:bg-red-500 focus:ring-red-500`}
            >
              {cancel.isPending ? 'Cancelling…' : 'Yes, cancel the order'}
            </button>
            <button
              type="button"
              autoFocus
              disabled={cancel.isPending}
              onClick={() => { setAsking(false) }}
              className={`${buttonClass} bg-white text-slate-800 ring-1 ring-slate-300 hover:bg-slate-50 focus:ring-indigo-500`}
            >
              Keep the order
            </button>
          </div>
        </div>
      ) : (
        <button
          type="button"
          onClick={() => { setAsking(true) }}
          className={`${buttonClass} self-start bg-white text-red-700 ring-1 ring-red-300 hover:bg-red-50 focus:ring-red-500`}
        >
          Cancel order
        </button>
      )}
    </div>
  )
}
