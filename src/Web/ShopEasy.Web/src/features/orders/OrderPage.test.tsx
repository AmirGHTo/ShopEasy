import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { expectNoAccessibilityViolations } from '../../test/a11y'
import { nobodyIsSignedIn, signedInAs } from '../../test/gateway'
import { anOrderSummary, theOrderMovesThrough } from '../../test/orders'
import { renderApp, renderRoute } from '../../test/render'
import { server } from '../../test/server'
import OrderPage from './OrderPage'

const orderId = '00000000-0000-0000-0000-0000000000f1'

// the page on its own route, looking again every 20 ms and giving up after 4 looks
function openOrder(polling: { pollMs?: number; maxPolls?: number } = {}) {
  server.use(signedInAs())
  return renderRoute('/orders/:id', <OrderPage pollMs={20} maxPolls={4} {...polling} />, `/orders/${orderId}`)
}

const wait = (ms: number) => new Promise<void>((resolve) => { setTimeout(resolve, ms) })

describe('OrderPage', () => {
  it('shows a Pending order as being processed, and moves on when it is confirmed', async () => {
    const order = theOrderMovesThrough(orderId, [
      anOrderSummary(orderId),
      anOrderSummary(orderId),
      anOrderSummary(orderId, { statusName: 'Confirmed', paymentStatus: 'Succeeded' }),
    ])
    openOrder()

    expect(await screen.findByText('We are processing your order.')).toBeInTheDocument()
    expect(await screen.findByText('Your order is confirmed.')).toBeInTheDocument()
    expect(screen.getByText('Succeeded')).toBeInTheDocument() // the payment
    expect(order.looks()).toBe(3)
  })

  it('stops looking once the order has settled', async () => {
    const order = theOrderMovesThrough(orderId, [anOrderSummary(orderId, { statusName: 'Confirmed' })])
    openOrder()
    await screen.findByText('Your order is confirmed.')

    await wait(150) // plenty of 20 ms looks

    expect(order.looks()).toBe(1)
  })

  it('shows what the order is: number, time, items and total', async () => {
    theOrderMovesThrough(orderId, [anOrderSummary(orderId, { statusName: 'Confirmed' })])
    const { container } = openOrder()

    expect(await screen.findByText(orderId)).toBeInTheDocument()
    expect(screen.getByText('Standing Desk x1, Desk Lamp x2')).toBeInTheDocument()
    expect(screen.getByText('$678.99')).toBeInTheDocument()
    expect(screen.getByText('Placed')).toBeInTheDocument()
    expect(screen.getByText(/2026/)).toBeInTheDocument() // when it was placed
    expect(screen.getByRole('link', { name: 'Continue shopping' })).toHaveAttribute('href', '/products')
    await expectNoAccessibilityViolations(container)
  })

  it('says why an order was cancelled', async () => {
    theOrderMovesThrough(orderId, [anOrderSummary(orderId, { statusName: 'Cancelled', failureReason: 'Not enough stock' })])
    openOrder()

    expect(await screen.findByText('Your order was cancelled.')).toBeInTheDocument()
    expect(screen.getByText('Reason: Not enough stock')).toBeInTheDocument()
  })

  it('says when the payment failed', async () => {
    theOrderMovesThrough(orderId, [anOrderSummary(orderId, { statusName: 'PaymentFailed', paymentStatus: 'Failed' })])
    openOrder()

    expect(await screen.findByText('The payment failed, so your order was not completed.')).toBeInTheDocument()
  })

  it('shows a status it does not know as it is, and stops looking', async () => {
    const order = theOrderMovesThrough(orderId, [anOrderSummary(orderId, { statusName: 'OnTheMoon' })])
    openOrder()

    expect(await screen.findByText('Status: OnTheMoon')).toBeInTheDocument()
    await wait(100)
    expect(order.looks()).toBe(1)
  })

  it('does not show the reason of an order that is fine', async () => {
    theOrderMovesThrough(orderId, [anOrderSummary(orderId, { statusName: 'Confirmed', failureReason: null })])
    openOrder()

    await screen.findByText('Your order is confirmed.')

    expect(screen.queryByText(/Reason:/)).not.toBeInTheDocument()
  })

  it('stops after the last look, says it is taking longer, and lets the customer look again', async () => {
    const user = userEvent.setup()
    const order = theOrderMovesThrough(orderId, [
      anOrderSummary(orderId),
      anOrderSummary(orderId),
      anOrderSummary(orderId),
      anOrderSummary(orderId),
      anOrderSummary(orderId, { statusName: 'Confirmed' }),
    ])
    openOrder({ maxPolls: 4 })

    expect(await screen.findByText('This is taking longer than usual.')).toBeInTheDocument()
    await wait(120)
    expect(order.looks()).toBe(4) // it gave up: no more looks by itself

    await user.click(screen.getByRole('button', { name: 'Refresh' }))

    expect(await screen.findByText('Your order is confirmed.')).toBeInTheDocument()
  })

  it('says so for an order that does not exist (or is not theirs), without looking again', async () => {
    let looks = 0
    server.use(
      signedInAs(),
      http.get(`/api/orders/${orderId}`, () => {
        looks += 1
        return new HttpResponse(null, { status: 404 })
      }),
    )
    renderRoute('/orders/:id', <OrderPage pollMs={20} />, `/orders/${orderId}`)

    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeInTheDocument()
    expect(screen.getByText('There is no such order.')).toBeInTheDocument()
    await wait(100)
    expect(looks).toBe(1)
  })

  it('does not even ask for something that is not an order id', async () => {
    let asked = false
    server.use(
      signedInAs(),
      http.get('/api/orders/:id', () => {
        asked = true
        return new HttpResponse(null, { status: 404 })
      }),
    )
    renderRoute('/orders/:id', <OrderPage />, '/orders/..%2F..%2Fadmin')

    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeInTheDocument()
    expect(asked).toBe(false)
  })

  it('asks a visitor to sign in', async () => {
    server.use(nobodyIsSignedIn(), http.get(`/api/orders/${orderId}`, () => HttpResponse.json({ error: 'not_signed_in' }, { status: 401 })))
    renderRoute('/orders/:id', <OrderPage />, `/orders/${orderId}`)

    expect(await screen.findByText('Sign in to see your order.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', `/account/login?returnUrl=%2Forders%2F${orderId}`)
  })

  it('says so when the order cannot be loaded, and tries again', async () => {
    const user = userEvent.setup()
    server.use(signedInAs(), http.get(`/api/orders/${orderId}`, () => HttpResponse.json({ message: 'Down.' }, { status: 503 })))
    renderRoute('/orders/:id', <OrderPage />, `/orders/${orderId}`)
    expect(await screen.findByRole('alert')).toHaveTextContent('We could not load your order. It has not been lost.')

    theOrderMovesThrough(orderId, [anOrderSummary(orderId, { statusName: 'Confirmed' })]) // the shop is back
    await user.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByText('Your order is confirmed.')).toBeInTheDocument()
  })

  it('is on the address /orders/:id of the app', async () => {
    theOrderMovesThrough(orderId, [anOrderSummary(orderId, { statusName: 'Confirmed' })])
    server.use(signedInAs())

    renderApp(`/orders/${orderId}`)

    await waitFor(() => { expect(document.title).toBe('Your order · ShopEasy') })
    expect(await screen.findByText('Your order is confirmed.')).toBeInTheDocument()
  })
})
