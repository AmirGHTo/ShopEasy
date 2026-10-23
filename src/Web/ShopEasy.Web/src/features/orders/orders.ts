import { z } from 'zod'
import { ApiError, request } from '../../api/http'
import type { CartLine } from '../cart/cart'

// The Order API takes at most this many characters of notes (OrderLimits in the Order service)
export const NOTES_MAX_LENGTH = 500

// What POST /api/orders answers with (201): the order as the server priced it. The prices of the
// cart are only an estimate; these are what counts.
const placedOrderSchema = z.object({
  id: z.guid(),
  totalAmount: z.number(),
  items: z.array(
    z.object({
      productId: z.guid(),
      productName: z.string(),
      quantity: z.int(),
      unitPrice: z.number(),
      lineTotal: z.number(),
    }),
  ),
})

export type PlacedOrder = z.infer<typeof placedOrderSchema>

// Sends the cart's lines and the note. Only the product and the quantity are sent: the server finds
// the name and the price itself and ignores any the client might send.
//
// The Idempotency-Key makes a repeat safe. If the answer is lost (the connection drops, the page is
// reloaded) the same call with the same key gives back the order that was placed instead of placing a
// second one, see checkoutAttempt.
export function placeOrder(
  order: { lines: readonly CartLine[]; notes: string; idempotencyKey: string },
  signal?: AbortSignal,
): Promise<PlacedOrder> {
  const notes = order.notes.trim()

  return request('/api/orders', placedOrderSchema, {
    method: 'POST',
    body: {
      items: order.lines.map(({ productId, quantity }) => ({ productId, quantity })),
      ...(notes === '' ? {} : { notes }),
    },
    headers: { 'Idempotency-Key': order.idempotencyKey },
    signal,
  })
}

// An order as the list of orders (the read model of the Order service) shows it
const orderSummarySchema = z.object({
  orderId: z.guid(),
  statusName: z.string(),
  totalAmount: z.number(),
  amountDisplay: z.string(),
  itemCount: z.int(),
  itemsSummary: z.string(),
  paymentStatus: z.string().nullable(),
  failureReason: z.string().nullable(),
  placedAt: z.string(),
  updatedAt: z.string(),
})

export type OrderSummary = z.infer<typeof orderSummarySchema>

// null when there is no such order of this customer (or the id is not even an id)
export async function fetchOrder(id: string, signal?: AbortSignal): Promise<OrderSummary | null> {
  if (!z.guid().safeParse(id).success) return null

  try {
    return await request(`/api/orders/${id}`, orderSummarySchema, { signal })
  } catch (error) {
    if (error instanceof ApiError && error.status === 404) return null
    throw error
  }
}

// An order is placed as Pending and moves on by itself: stock is reserved and the payment taken, then
// it is Confirmed, or Cancelled / PaymentFailed when a step failed. Anything but Pending has settled.
export function isSettled(statusName: string): boolean {
  return statusName !== 'Pending'
}
