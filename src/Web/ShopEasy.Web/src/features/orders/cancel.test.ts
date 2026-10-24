import { describe, expect, it } from 'vitest'
import { ApiError, UnexpectedResponseError } from '../../api/http'
import { canCancel, describeCancelProblem } from './cancel'

describe('canCancel', () => {
  it.each(['Pending', 'Confirmed'])('offers cancelling for a %s order', (status) => {
    expect(canCancel(status)).toBe(true)
  })

  it.each(['Cancelled', 'PaymentFailed', 'Shipped', 'Delivered', 'OnTheMoon', ''])('does not offer it for %s', (status) => {
    expect(canCancel(status)).toBe(false)
  })
})

describe('describeCancelProblem', () => {
  it('asks for a new sign-in when the session has ended', () => {
    const problem = describeCancelProblem(new ApiError(401, 'x', 'not_signed_in', [], undefined))

    expect(problem.title).toBe('Your session has ended. Sign in again to cancel the order.')
    expect(problem.retryIsSafe).toBe(false)
  })

  it('says when the order is not there', () => {
    expect(describeCancelProblem(new ApiError(404, 'x', undefined, [], undefined)).title).toBe('This order could not be found.')
  })

  it('passes on the reason the shop gives when it is too late', () => {
    const problem = describeCancelProblem(new ApiError(409, 'Order has already shipped.', undefined, [], undefined))

    expect(problem.title).toBe('Order has already shipped.')
    expect(problem.retryIsSafe).toBe(false)
  })

  it('says how long to wait when the customer is going too fast', () => {
    const problem = describeCancelProblem(new ApiError(429, 'x', 'rate_limited', [], 30))

    expect(problem.title).toBe('You are doing that too quickly.')
    expect(problem.retryAfterSeconds).toBe(30)
    expect(problem.retryIsSafe).toBe(true)
  })

  it('says nothing was changed for a failure of the shop', () => {
    const problem = describeCancelProblem(new ApiError(503, 'Down.', undefined, [], undefined))

    expect(problem.title).toBe('We could not cancel the order. Nothing was changed. Try again.')
    expect(problem.retryIsSafe).toBe(true)
  })

  it.each([new TypeError('Failed to fetch'), new UnexpectedResponseError('/api/orders/1', [])])(
    'says that cancelling twice does no harm when the answer was lost (%s)',
    (error) => {
      const problem = describeCancelProblem(error)

      expect(problem.title).toContain('cancelling twice does no harm')
      expect(problem.retryIsSafe).toBe(true)
    },
  )

  it('has words for anything else', () => {
    expect(describeCancelProblem('strange').title).toBe('Something went wrong. Try again.')
  })
})
