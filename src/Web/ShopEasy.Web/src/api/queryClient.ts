import { QueryClient } from '@tanstack/react-query'
import { ApiError, UnexpectedResponseError } from './http'

// An answer from the gateway (a 4xx) or one of the wrong shape will not change on a second try; a
// dropped connection or a 5xx may.
export function shouldRetry(failureCount: number, error: Error): boolean {
  if (error instanceof UnexpectedResponseError) return false
  if (error instanceof ApiError && error.status < 500) return false
  return failureCount < 2
}

export function createQueryClient(): QueryClient {
  return new QueryClient({ defaultOptions: { queries: { retry: shouldRetry } } })
}
