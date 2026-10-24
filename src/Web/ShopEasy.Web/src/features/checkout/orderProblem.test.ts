import { describe, expect, it } from 'vitest'
import { ApiError, UnexpectedResponseError } from '../../api/http'
import { describeOrderProblem } from './orderProblem'

describe('describeOrderProblem', () => {
  it('lists what the shop said was wrong with the order (400)', () => {
    const problem = describeOrderProblem(
      new ApiError(400, 'Order could not be placed.', undefined, ['Item 1: quantity must be at least 1.', 'Product x does not exist.']),
    )

    expect(problem.kind).toBe('invalid')
    expect(problem.details).toEqual(['Item 1: quantity must be at least 1.', 'Product x does not exist.'])
    expect(problem.retryIsSafe).toBe(true)
  })

  it('falls back to the shop’s message when it listed nothing (400)', () => {
    expect(describeOrderProblem(new ApiError(400, 'A malformed key.')).details).toEqual(['A malformed key.'])
  })

  it('asks the customer to sign in again when the session ended (401), and does not offer a retry', () => {
    const problem = describeOrderProblem(new ApiError(401, 'x'))

    expect(problem.kind).toBe('signed-out')
    expect(problem.retryIsSafe).toBe(false)
  })

  it('says how long to wait when orders come too fast (429)', () => {
    const problem = describeOrderProblem(new ApiError(429, 'x', undefined, [], 30))

    expect(problem).toMatchObject({ kind: 'too-fast', retryAfterSeconds: 30, retryIsSafe: true })
  })

  it.each([502, 503, 504])('says that nothing was ordered when the shop could not check the prices (%s)', (status) => {
    const problem = describeOrderProblem(new ApiError(status, 'x', undefined, [], 5))

    expect(problem).toMatchObject({ kind: 'unavailable', retryAfterSeconds: 5, retryIsSafe: true })
    expect(problem.title).toContain('Nothing was ordered')
  })

  it('treats a reused key as a changed order (422)', () => {
    expect(describeOrderProblem(new ApiError(422, 'x')).kind).toBe('changed')
  })

  it('does not pretend to know when something else failed (500)', () => {
    const problem = describeOrderProblem(new ApiError(500, 'x'))

    expect(problem.kind).toBe('failed')
    expect(problem.title).toContain('may not have been placed')
  })

  it.each([
    ['a dropped connection', new TypeError('Failed to fetch')],
    ['an answer that could not be read', new UnexpectedResponseError('/api/orders', [])],
  ])('says the order may or may not be placed, and that placing it again is safe, for %s', (_what, error) => {
    const problem = describeOrderProblem(error)

    expect(problem.kind).toBe('unclear')
    expect(problem.details.join(' ')).toContain('not be ordered twice')
    expect(problem.retryIsSafe).toBe(true)
  })

  it('copes with anything that is thrown', () => {
    expect(describeOrderProblem('what')).toMatchObject({ kind: 'failed', details: [] })
  })
})
