import { http, HttpResponse } from 'msw'
import { server } from './server'

// An order as POST /api/orders answers (201), priced by the server
export function aPlacedOrder(id: string, totalAmount = 649.97) {
  return {
    id,
    userId: 'u-1',
    userEmail: 'alice@example.com',
    status: 0,
    totalAmount,
    notes: null,
    createdAt: '2026-10-23T10:00:00Z',
    updatedAt: '2026-10-23T10:00:00Z',
    items: [
      {
        id: '00000000-0000-0000-0000-0000000000a1',
        productId: '00000000-0000-0000-0000-000000000003',
        productName: 'Standing Desk',
        quantity: 1,
        unitPrice: totalAmount,
        lineTotal: totalAmount,
      },
    ],
  }
}

export interface SentOrder {
  idempotencyKey: string | null
  body: { items: { productId: string; quantity: number }[]; notes?: string }
}

// The Order API's POST: every request that arrives is noted, and answered by `answer` (given the
// number of the request, 1 for the first)
export function orderApiAnswers(answer: (attempt: number) => Response | Promise<Response>): SentOrder[] {
  const sent: SentOrder[] = []
  server.use(
    http.post('/api/orders', async ({ request }) => {
      sent.push({
        idempotencyKey: request.headers.get('Idempotency-Key'),
        body: (await request.json()) as SentOrder['body'],
      })
      return answer(sent.length)
    }),
  )
  return sent
}

export const created = (order: object) => HttpResponse.json(order, { status: 201 })

// An order as GET /api/orders/{id} answers (the read model), by default still Pending
export function anOrderSummary(orderId: string, overrides: Record<string, unknown> = {}) {
  return {
    orderId,
    userId: 'u-1',
    userEmail: 'alice@example.com',
    statusName: 'Pending',
    statusCode: 0,
    totalAmount: 678.99,
    amountDisplay: '$678.99',
    itemCount: 3,
    itemsSummary: 'Standing Desk x1, Desk Lamp x2',
    paymentStatus: null,
    failureReason: null,
    placedAt: '2026-10-23T10:00:00Z',
    updatedAt: '2026-10-23T10:00:00Z',
    ...overrides,
  }
}

// The order as the customer's later looks find it: each look gets the next answer, the last one is
// repeated. `looks()` is how many times it was asked.
export function theOrderMovesThrough(orderId: string, answers: object[]) {
  let looks = 0
  server.use(
    http.get(`/api/orders/${orderId}`, () => {
      const answer = answers[Math.min(looks, answers.length - 1)] ?? {}
      looks += 1
      return HttpResponse.json(answer)
    }),
  )
  return { looks: () => looks }
}
