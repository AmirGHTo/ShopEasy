import { describe, expect, it } from 'vitest'
import { ApiError } from '../api/http'
import { jsonResponse, stubFetch } from '../test/fetch'
import { fetchSession, signInUrl } from './session'

const alice = { isAuthenticated: true, userId: 'u-1', email: 'alice@example.com', name: 'Alice', roles: ['customer'] }

describe('fetchSession', () => {
  it('returns who is signed in', async () => {
    const fetchMock = stubFetch(jsonResponse(alice))

    await expect(fetchSession()).resolves.toMatchObject({ userId: 'u-1', email: 'alice@example.com', roles: ['customer'] })
    expect(fetchMock.mock.calls[0][0]).toBe('/account/me')
  })

  it('returns null when nobody is signed in', async () => {
    stubFetch(new Response(null, { status: 401 }))

    await expect(fetchSession()).resolves.toBeNull()
  })

  it('does not hide any other failure', async () => {
    stubFetch(jsonResponse({ message: 'Down.' }, 503))

    await expect(fetchSession()).rejects.toBeInstanceOf(ApiError)
  })
})

describe('signInUrl', () => {
  it('asks the gateway to come back to the page the visitor is on', () => {
    expect(signInUrl('/products?q=desk lamp')).toBe('/account/login?returnUrl=%2Fproducts%3Fq%3Ddesk%20lamp')
  })
})
