import { ApiError, request } from '../api/http'

// Who the gateway thinks is using this browser (/account/me)
export interface Session {
  userId: string
  email: string
  name: string | null
  roles: string[]
}

// null when nobody is signed in, which is an ordinary answer and not an error
export async function fetchSession(signal?: AbortSignal): Promise<Session | null> {
  try {
    return await request<Session>('/account/me', { signal })
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) return null
    throw error
  }
}

// Signing in and out are page loads, not API calls: the gateway sends the browser to Identity and
// back. It only returns to a path on this site, so pass the current one.
export function signInUrl(returnTo: string): string {
  return `/account/login?returnUrl=${encodeURIComponent(returnTo)}`
}

export const signOutUrl = '/account/logout'
