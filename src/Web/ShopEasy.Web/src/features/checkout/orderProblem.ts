import { ApiError, UnexpectedResponseError } from '../../api/http'

// What went wrong when placing an order, in the words the customer needs. `kind` says what the page
// has to offer (a sign-in link, a wait, nothing but a retry); `details` are lines from the shop, shown
// as a list.
export type OrderProblemKind =
  | 'invalid' // 400: the order cannot be placed as it is (an unknown or sold-out product, ...)
  | 'signed-out' // 401: the session ended
  | 'too-fast' // 429: too many orders in a minute
  | 'unavailable' // 502/503/504: the shop could not price the order right now, nothing was saved
  | 'unclear' // the answer was lost or unreadable: the order may or may not have been placed
  | 'changed' // 422: the key was used for a different order (cannot happen from the page)
  | 'failed' // anything else

export interface OrderProblem {
  kind: OrderProblemKind
  title: string
  details: readonly string[]
  // how long the shop asked to wait, when it did
  retryAfterSeconds: number | undefined
  // whether pressing "Place order" again is the right thing: the same key is used, so it can
  // never place the order twice
  retryIsSafe: boolean
}

export function describeOrderProblem(error: unknown): OrderProblem {
  if (error instanceof ApiError) {
    const wait = error.retryAfterSeconds
    switch (error.status) {
      case 400:
        return problem('invalid', 'Your order could not be placed.', error.details.length > 0 ? error.details : [error.message], wait, true)
      case 401:
        return problem('signed-out', 'Your session has ended. Sign in again to place your order.', [], wait, false)
      case 422:
        return problem('changed', 'This order was already sent in a different form. Review it and place it again.', [], wait, true)
      case 429:
        return problem('too-fast', 'You are placing orders too quickly.', [], wait, true)
      case 502:
      case 503:
      case 504:
        return problem('unavailable', 'The shop could not check the prices right now. Nothing was ordered.', [], wait, true)
      default:
        return problem('failed', 'Something went wrong. Your order may not have been placed.', [], wait, true)
    }
  }

  // a dropped connection (TypeError from fetch), or an answer that could not be read
  if (error instanceof TypeError || error instanceof UnexpectedResponseError) {
    return problem(
      'unclear',
      'We could not confirm that your order was placed.',
      ['It may or may not have been. Place it again: that is safe, it will not be ordered twice.'],
      undefined,
      true,
    )
  }

  return problem('failed', 'Something went wrong. Your order may not have been placed.', [], undefined, true)
}

function problem(
  kind: OrderProblemKind,
  title: string,
  details: readonly string[],
  retryAfterSeconds: number | undefined,
  retryIsSafe: boolean,
): OrderProblem {
  return { kind, title, details, retryAfterSeconds, retryIsSafe }
}
