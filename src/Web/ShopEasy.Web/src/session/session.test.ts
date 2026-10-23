import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { ApiError, UnexpectedResponseError } from '../api/http'
import { alice, nobodyIsSignedIn, signedInAs } from '../test/gateway'
import { server } from '../test/server'
import { fetchSession, signInUrl } from './session'

describe('fetchSession', () => {
  it('returns who is signed in, with only what the app uses', async () => {
    server.use(signedInAs())

    await expect(fetchSession()).resolves.toStrictEqual({
      userId: 'u-1',
      email: 'alice@example.com',
      name: 'Alice',
      roles: ['customer'],
    })
  })

  it('refuses an answer that is not a session', async () => {
    server.use(signedInAs({ userId: 42, roles: 'customer' }))

    await expect(fetchSession()).rejects.toBeInstanceOf(UnexpectedResponseError)
  })

  it('accepts an account with neither name nor email', async () => {
    server.use(signedInAs({ ...alice, name: null, email: null }))

    await expect(fetchSession()).resolves.toMatchObject({ name: null, email: null })
  })

  it('returns null when nobody is signed in', async () => {
    server.use(nobodyIsSignedIn())

    await expect(fetchSession()).resolves.toBeNull()
  })

  it('does not hide any other failure', async () => {
    server.use(http.get('/account/me', () => HttpResponse.json({ message: 'Down.' }, { status: 503 })))

    await expect(fetchSession()).rejects.toBeInstanceOf(ApiError)
  })
})

describe('signInUrl', () => {
  it('asks the gateway to come back to the page the visitor is on', () => {
    expect(signInUrl('/products?q=desk lamp')).toBe('/account/login?returnUrl=%2Fproducts%3Fq%3Ddesk%20lamp')
  })
})
