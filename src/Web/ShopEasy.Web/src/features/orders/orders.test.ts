import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { ApiError, UnexpectedResponseError } from '../../api/http'
import { server } from '../../test/server'
import { fetchOrder, isSettled, placeOrder } from './orders'

const id = (n: number) => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`

const placed = {
  id: id(77),
  userId: 'u-1',
  userEmail: 'alice@example.com',
  status: 0,
  totalAmount: 649.97,
  notes: null,
  createdAt: '2026-10-23T10:00:00Z',
  items: [
    { id: id(1), productId: id(3), productName: 'Standing Desk', quantity: 1, unitPrice: 599.99, lineTotal: 599.99 },
    { id: id(2), productId: id(5), productName: '4K Webcam', quantity: 1, unitPrice: 49.98, lineTotal: 49.98 },
  ],
}

const summary = {
  orderId: id(77),
  userId: 'u-1',
  userEmail: 'alice@example.com',
  statusName: 'Pending',
  statusCode: 0,
  totalAmount: 649.97,
  amountDisplay: '$649.97',
  itemCount: 2,
  itemsSummary: 'Standing Desk x1, 4K Webcam x1',
  paymentStatus: null,
  failureReason: null,
  placedAt: '2026-10-23T10:00:00Z',
  updatedAt: '2026-10-23T10:00:00Z',
}

interface Sent {
  headers: Headers
  body: unknown
}

function theOrderApiAnswers(response: () => Response): Sent[] {
  const sent: Sent[] = []
  server.use(
    http.post('/api/orders', async ({ request }) => {
      sent.push({ headers: request.headers, body: await request.json() })
      return response()
    }),
  )
  return sent
}

describe('placeOrder', () => {
  const lines = [
    { productId: id(3), quantity: 1 },
    { productId: id(5), quantity: 2 },
  ]

  it('sends only the products and quantities, with the key, and returns the order the server priced', async () => {
    const sent = theOrderApiAnswers(() => HttpResponse.json(placed, { status: 201 }))

    const order = await placeOrder({ lines, notes: '', idempotencyKey: 'key-1' })

    expect(sent).toHaveLength(1)
    expect(sent[0]?.body).toEqual({
      items: [
        { productId: id(3), quantity: 1 },
        { productId: id(5), quantity: 2 },
      ],
    })
    expect(sent[0]?.headers.get('Idempotency-Key')).toBe('key-1')
    expect(sent[0]?.headers.get('X-Requested-With')).toBe('ShopEasy.Web')
    expect(order.id).toBe(id(77))
    expect(order.totalAmount).toBe(649.97)
    expect(order.items.map((item) => item.productName)).toEqual(['Standing Desk', '4K Webcam'])
  })

  it('sends the note trimmed, and none when it is empty or only spaces', async () => {
    const sent = theOrderApiAnswers(() => HttpResponse.json(placed, { status: 201 }))

    await placeOrder({ lines, notes: '  leave at the door  ', idempotencyKey: 'k' })
    await placeOrder({ lines, notes: '   ', idempotencyKey: 'k' })

    expect(sent[0]?.body).toMatchObject({ notes: 'leave at the door' })
    expect(sent[1]?.body).not.toHaveProperty('notes')
  })

  it('never sends a name or a price: the server decides those', async () => {
    const sent = theOrderApiAnswers(() => HttpResponse.json(placed, { status: 201 }))

    await placeOrder({
      lines: [{ productId: id(3), quantity: 1, price: 0.01, productName: 'free' } as never],
      notes: '',
      idempotencyKey: 'k',
    })

    expect(JSON.stringify(sent[0]?.body)).not.toMatch(/price|productName|0\.01|free/i)
  })

  it('rejects with the list of problems the server gave', async () => {
    const refusal = {
      message: 'Order could not be placed.',
      errors: ['Product 00000000-0000-0000-0000-000000000099 does not exist.'],
    }
    theOrderApiAnswers(() => HttpResponse.json(refusal, { status: 400 }))

    const error = await placeOrder({ lines, notes: '', idempotencyKey: 'k' }).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ status: 400, details: refusal.errors })
  })

  it('rejects an answer that is not an order', async () => {
    theOrderApiAnswers(() => HttpResponse.json({ id: 'not-an-id' }, { status: 201 }))

    await expect(placeOrder({ lines, notes: '', idempotencyKey: 'k' })).rejects.toBeInstanceOf(UnexpectedResponseError)
  })

  it('tells how long to wait when the shop asks to', async () => {
    theOrderApiAnswers(() =>
      HttpResponse.json({ error: 'Too many requests', retryAfter: 60 }, { status: 429, headers: { 'Retry-After': '42' } }),
    )

    const error = await placeOrder({ lines, notes: '', idempotencyKey: 'k' }).catch((e: unknown) => e)

    expect(error).toMatchObject({ status: 429, retryAfterSeconds: 42 })
  })
})

describe('fetchOrder', () => {
  it('returns the order of this customer', async () => {
    server.use(http.get(`/api/orders/${id(77)}`, () => HttpResponse.json(summary)))

    await expect(fetchOrder(id(77))).resolves.toMatchObject({
      orderId: id(77),
      statusName: 'Pending',
      amountDisplay: '$649.97',
      paymentStatus: null,
    })
  })

  it('returns null for an order that is not there (or not theirs)', async () => {
    server.use(http.get(`/api/orders/${id(78)}`, () => new HttpResponse(null, { status: 404 })))

    await expect(fetchOrder(id(78))).resolves.toBeNull()
  })

  it('does not ask for something that is not an id', async () => {
    let asked = false
    server.use(
      http.get('/api/orders/:id', () => {
        asked = true
        return new HttpResponse(null, { status: 404 })
      }),
    )

    await expect(fetchOrder('../../admin')).resolves.toBeNull()
    expect(asked).toBe(false)
  })

  it('does not hide any other failure', async () => {
    server.use(http.get(`/api/orders/${id(77)}`, () => HttpResponse.json({}, { status: 503 })))

    await expect(fetchOrder(id(77))).rejects.toMatchObject({ status: 503 })
  })

  it('refuses an answer that is not an order summary', async () => {
    server.use(http.get(`/api/orders/${id(77)}`, () => HttpResponse.json({ orderId: id(77) })))

    await expect(fetchOrder(id(77))).rejects.toBeInstanceOf(UnexpectedResponseError)
  })
})

describe('isSettled', () => {
  it('is false only while the order is Pending', () => {
    expect(isSettled('Pending')).toBe(false)
    for (const status of ['Confirmed', 'Cancelled', 'PaymentFailed', 'Shipped', 'Delivered', 'SomethingNew']) {
      expect(isSettled(status)).toBe(true)
    }
  })
})
