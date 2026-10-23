import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { z } from 'zod'
import { server } from '../test/server'
import { ApiError, perform, request, UnexpectedResponseError } from './http'

const anything = z.unknown()
const item = z.object({ id: z.number() })

interface Seen {
  headers: Headers
  body: string
}

// Answers one address and notes what the app sent to it
function answer(method: 'get' | 'post' | 'put' | 'patch' | 'delete', path: string, response: () => Response): Seen[] {
  const seen: Seen[] = []
  server.use(
    http[method](path, async ({ request: sent }) => {
      seen.push({ headers: sent.headers, body: await sent.text() })
      return response()
    }),
  )
  return seen
}

describe('request', () => {
  it('reads JSON and sends no body or marker header on a read', async () => {
    const seen = answer('get', '/api/v1/products/7', () => HttpResponse.json({ id: 7 }))

    await expect(request('/api/v1/products/7', item)).resolves.toEqual({ id: 7 })

    expect(seen).toHaveLength(1)
    expect(seen[0]?.body).toBe('')
    expect(seen[0]?.headers.get('Accept')).toBe('application/json')
    expect(seen[0]?.headers.has('X-Requested-With')).toBe(false)
  })

  it('hands on only what the schema names', async () => {
    server.use(http.get('/api/v1/products/7', () => HttpResponse.json({ id: 7, internalNote: 'not for the page' })))

    await expect(request('/api/v1/products/7', item)).resolves.toStrictEqual({ id: 7 })
  })

  it('rejects an answer of the wrong shape, saying what is wrong', async () => {
    server.use(http.get('/api/v1/products/7', () => HttpResponse.json({ id: 'seven' })))

    const error = await request('/api/v1/products/7', item).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(UnexpectedResponseError)
    expect(error).toMatchObject({ path: '/api/v1/products/7' })
    expect((error as UnexpectedResponseError).issues[0]).toMatchObject({ path: ['id'], code: 'invalid_type' })
  })

  it('sends a body as JSON, with the headers the caller adds', async () => {
    const seen = answer('post', '/api/orders', () => HttpResponse.json({ id: 1 }, { status: 201 }))

    await request('/api/orders', item, {
      method: 'POST',
      body: { items: [{ productId: 'p-1', quantity: 2 }] },
      headers: { 'Idempotency-Key': 'key-1' },
    })

    expect(seen[0]?.body).toBe('{"items":[{"productId":"p-1","quantity":2}]}')
    expect(seen[0]?.headers.get('Content-Type')).toBe('application/json')
    expect(seen[0]?.headers.get('Idempotency-Key')).toBe('key-1')
    expect(seen[0]?.headers.get('X-Requested-With')).toBe('ShopEasy.Web')
  })

  it('rejects with the message, code and list of errors the service gave', async () => {
    const problem = { message: 'Order could not be placed.', errors: ['Quantity must be positive.', 7] }
    server.use(http.post('/api/orders', () => HttpResponse.json(problem, { status: 400 })))

    const error = await request('/api/orders', anything, { method: 'POST', body: {} }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({
      status: 400,
      message: 'Order could not be placed.',
      details: ['Quantity must be positive.'],
    })
  })

  it('reads the short code of the gateway', async () => {
    const refusal = { error: 'request_header_missing', message: 'Header missing.' }
    server.use(http.get('/api/orders', () => HttpResponse.json(refusal, { status: 403 })))

    const error = await request('/api/orders', anything).catch((e: unknown) => e)

    expect(error).toMatchObject({ status: 403, code: 'request_header_missing', message: 'Header missing.' })
  })

  it('falls back to the status when the body is not JSON', async () => {
    server.use(http.get('/api/v1/products', () => new HttpResponse('<html>Bad gateway</html>', { status: 502 })))

    const error = await request('/api/v1/products', anything).catch((e: unknown) => e)

    expect(error).toMatchObject({ status: 502, message: 'The request failed (502).', details: [] })
  })

  it('lets a dropped connection through as the fetch error it is', async () => {
    server.use(http.get('/api/v1/products', () => HttpResponse.error()))

    await expect(request('/api/v1/products', anything)).rejects.toBeInstanceOf(TypeError)
  })
})

describe('perform', () => {
  it.each(['post', 'put', 'patch', 'delete'] as const)('marks a %s as coming from the shop', async (method) => {
    const seen = answer(method, '/api/orders', () => new HttpResponse(null, { status: 204 }))

    await perform('/api/orders', { method: method.toUpperCase() as Uppercase<typeof method> })

    expect(seen[0]?.headers.get('X-Requested-With')).toBe('ShopEasy.Web')
  })

  it('resolves with nothing on 204 No Content', async () => {
    server.use(http.delete('/api/orders/1', () => new HttpResponse(null, { status: 204 })))

    await expect(perform('/api/orders/1', { method: 'DELETE' })).resolves.toBeUndefined()
  })

  it('rejects like request does when the service refuses', async () => {
    server.use(http.delete('/api/orders/1', () => HttpResponse.json({ message: 'Already shipped.' }, { status: 409 })))

    await expect(perform('/api/orders/1', { method: 'DELETE' })).rejects.toMatchObject({ status: 409 })
  })
})
