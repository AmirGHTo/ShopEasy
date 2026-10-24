import { ApiError, perform, UnexpectedResponseError } from '../../api/http'

// Only an order that is still being processed or has been confirmed can be cancelled. A cancelled one
// has nothing left to cancel, a failed payment is already over, and a shipped or delivered order
// cannot be called back (the Order API answers 409).
export function canCancel(statusName: string): boolean {
  return statusName === 'Pending' || statusName === 'Confirmed'
}

// Asks the Order API to cancel. It answers 204 when the order is cancelled on the spot, or 202 when
// the stock is being released and a payment refunded (it is then Cancelled a moment later); asking
// again is safe, so a retry after a lost answer needs no key.
export function cancelOrder(orderId: string, signal?: AbortSignal): Promise<void> {
  return perform(`/api/orders/${orderId}`, { method: 'DELETE', signal })
}

export interface CancelProblem {
  title: string
  // true when the customer can press the button again and it is the right thing to do
  retryIsSafe: boolean
  retryAfterSeconds: number | undefined
}

// What went wrong when cancelling, in the words the customer needs
export function describeCancelProblem(error: unknown): CancelProblem {
  if (error instanceof ApiError) {
    switch (error.status) {
      case 401:
        return { title: 'Your session has ended. Sign in again to cancel the order.', retryIsSafe: false, retryAfterSeconds: undefined }
      case 404:
        return { title: 'This order could not be found.', retryIsSafe: false, retryAfterSeconds: undefined }
      case 409:
        // the shop says why (it has shipped, ...): its words are the best explanation
        return { title: error.message, retryIsSafe: false, retryAfterSeconds: undefined }
      case 429:
        return { title: 'You are doing that too quickly.', retryIsSafe: true, retryAfterSeconds: error.retryAfterSeconds }
      default:
        return { title: 'We could not cancel the order. Nothing was changed. Try again.', retryIsSafe: true, retryAfterSeconds: undefined }
    }
  }

  if (error instanceof TypeError || error instanceof UnexpectedResponseError) {
    return {
      title: 'We could not confirm that the order was cancelled. Try again: cancelling twice does no harm.',
      retryIsSafe: true,
      retryAfterSeconds: undefined,
    }
  }

  return { title: 'Something went wrong. Try again.', retryIsSafe: true, retryAfterSeconds: undefined }
}
