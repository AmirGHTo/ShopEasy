import { describe, expect, it } from 'vitest'
import { ApiError } from './http'
import { shouldRetry } from './queryClient'

describe('shouldRetry', () => {
  it('does not repeat a request the gateway has answered with a 4xx', () => {
    expect(shouldRetry(0, new ApiError(404, 'Not found'))).toBe(false)
  })

  it('repeats a 5xx or a dropped connection, twice', () => {
    expect(shouldRetry(0, new ApiError(503, 'Unavailable'))).toBe(true)
    expect(shouldRetry(1, new TypeError('Failed to fetch'))).toBe(true)
    expect(shouldRetry(2, new TypeError('Failed to fetch'))).toBe(false)
  })
})
