import { describe, expect, it } from 'vitest'
import { jsonResponse, sentRequest, stubFetch } from '../test/fetch'
import { ApiError, request } from './http'

describe('request', () => {
  it('reads JSON and sends no body or marker header on a read', async () => {
    const fetchMock = stubFetch(jsonResponse({ id: 7 }))

    const result = await request<{ id: number }>('/api/v1/products/7')

    expect(result).toEqual({ id: 7 })
    const { path, init, headers } = sentRequest(fetchMock)
    expect(path).toBe('/api/v1/products/7')
    expect(init?.method).toBe('GET')
    expect(init?.body).toBeUndefined()
    expect(headers).toEqual({ Accept: 'application/json' })
  })

  it.each(['POST', 'PUT', 'PATCH', 'DELETE'] as const)('marks a %s as coming from the shop', async (method) => {
    const fetchMock = stubFetch(new Response(null, { status: 204 }))

    await request<undefined>('/api/orders', { method })

    expect(sentRequest(fetchMock).headers['X-Requested-With']).toBe('ShopEasy.Web')
  })

  it('sends a body as JSON, with the headers the caller adds', async () => {
    const fetchMock = stubFetch(jsonResponse({ id: 'o-1' }, 201))

    await request('/api/orders', {
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

  it('resolves with nothing on 204 No Content', async () => {
    stubFetch(new Response(null, { status: 204 }))

    await expect(request<undefined>('/api/orders/1', { method: 'DELETE' })).resolves.toBeUndefined()
  })

  it('rejects with the message, code and list of errors the service gave', async () => {
    stubFetch(jsonResponse({ message: 'Order could not be placed.', errors: ['Quantity must be positive.', 7] }, 400))

    const error = await request('/api/orders', { method: 'POST', body: {} }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({
      status: 400,
      message: 'Order could not be placed.',
      details: ['Quantity must be positive.'],
    })
  })

  it('reads the short code of the gateway', async () => {
    stubFetch(jsonResponse({ error: 'request_header_missing', message: 'Header missing.' }, 403))

    const error = await request('/api/orders').catch((e: unknown) => e)

    expect(error).toMatchObject({ status: 403, code: 'request_header_missing', message: 'Header missing.' })
  })

  it('falls back to the status when the body is not JSON', async () => {
    stubFetch(new Response('<html>Bad gateway</html>', { status: 502 }))

    const error = await request('/api/v1/products').catch((e: unknown) => e)

    expect(error).toMatchObject({ status: 502, message: 'The request failed (502).', details: [] })
  })

  it('lets a dropped connection through as it is', async () => {
    const dropped = new TypeError('Failed to fetch')
    const fetchMock = stubFetch()
    fetchMock.mockRejectedValueOnce(dropped)

    await expect(request('/api/v1/products')).rejects.toBe(dropped)
  })
})
