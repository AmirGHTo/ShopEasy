import { describe, expect, it } from 'vitest'
import { z } from 'zod'
import { jsonResponse, sentRequest, stubFetch } from '../test/fetch'
import { ApiError, perform, request, UnexpectedResponseError } from './http'

const anything = z.unknown()
const item = z.object({ id: z.number() })

describe('request', () => {
  it('reads JSON and sends no body or marker header on a read', async () => {
    const fetchMock = stubFetch(jsonResponse({ id: 7 }))

    const result = await request('/api/v1/products/7', item)

    expect(result).toEqual({ id: 7 })
    const { path, init, headers } = sentRequest(fetchMock)
    expect(path).toBe('/api/v1/products/7')
    expect(init?.method).toBe('GET')
    expect(init?.body).toBeUndefined()
    expect(headers).toEqual({ Accept: 'application/json' })
  })

  it('hands on only what the schema names', async () => {
    stubFetch(jsonResponse({ id: 7, internalNote: 'not for the page' }))

    await expect(request('/api/v1/products/7', item)).resolves.toStrictEqual({ id: 7 })
  })

  it('rejects an answer of the wrong shape, saying what is wrong', async () => {
    stubFetch(jsonResponse({ id: 'seven' }))

    const error = await request('/api/v1/products/7', item).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(UnexpectedResponseError)
    expect(error).toMatchObject({ path: '/api/v1/products/7' })
    expect((error as UnexpectedResponseError).issues[0]).toMatchObject({ path: ['id'], code: 'invalid_type' })
  })

  it('sends a body as JSON, with the headers the caller adds', async () => {
    const fetchMock = stubFetch(jsonResponse({ id: 1 }, 201))

    await request('/api/orders', item, {
      method: 'POST',
      body: { items: [{ productId: 'p-1', quantity: 2 }] },
      headers: { 'Idempotency-Key': 'key-1' },
    })

    const { init, headers } = sentRequest(fetchMock)
    expect(init?.body).toBe('{"items":[{"productId":"p-1","quantity":2}]}')
    expect(headers).toMatchObject({
      'Content-Type': 'application/json',
      'Idempotency-Key': 'key-1',
      'X-Requested-With': 'ShopEasy.Web',
    })
  })

  it('rejects with the message, code and list of errors the service gave', async () => {
    stubFetch(jsonResponse({ message: 'Order could not be placed.', errors: ['Quantity must be positive.', 7] }, 400))

    const error = await request('/api/orders', anything, { method: 'POST', body: {} }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({
      status: 400,
      message: 'Order could not be placed.',
      details: ['Quantity must be positive.'],
    })
  })

  it('reads the short code of the gateway', async () => {
    stubFetch(jsonResponse({ error: 'request_header_missing', message: 'Header missing.' }, 403))

    const error = await request('/api/orders', anything).catch((e: unknown) => e)

    expect(error).toMatchObject({ status: 403, code: 'request_header_missing', message: 'Header missing.' })
  })

  it('falls back to the status when the body is not JSON', async () => {
    stubFetch(new Response('<html>Bad gateway</html>', { status: 502 }))

    const error = await request('/api/v1/products', anything).catch((e: unknown) => e)

    expect(error).toMatchObject({ status: 502, message: 'The request failed (502).', details: [] })
  })

  it('lets a dropped connection through as it is', async () => {
    const dropped = new TypeError('Failed to fetch')
    const fetchMock = stubFetch()
    fetchMock.mockRejectedValueOnce(dropped)

    await expect(request('/api/v1/products', anything)).rejects.toBe(dropped)
  })
})

describe('perform', () => {
  it.each(['POST', 'PUT', 'PATCH', 'DELETE'] as const)('marks a %s as coming from the shop', async (method) => {
    const fetchMock = stubFetch(new Response(null, { status: 204 }))

    await perform('/api/orders', { method })

    expect(sentRequest(fetchMock).headers['X-Requested-With']).toBe('ShopEasy.Web')
  })

  it('resolves with nothing on 204 No Content', async () => {
    stubFetch(new Response(null, { status: 204 }))

    await expect(perform('/api/orders/1', { method: 'DELETE' })).resolves.toBeUndefined()
  })

  it('rejects like request does when the service refuses', async () => {
    stubFetch(jsonResponse({ message: 'Already shipped.' }, 409))

    await expect(perform('/api/orders/1', { method: 'DELETE' })).rejects.toMatchObject({ status: 409 })
  })
})
