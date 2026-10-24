import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { expectNoAccessibilityViolations } from '../../test/a11y'
import { signedInAs } from '../../test/gateway'
import { anOrderSummary, cancelApiAnswers, cancelled, cancelling, theOrderMovesThrough, theOrdersAre } from '../../test/orders'
import { renderApp, renderRoute } from '../../test/render'
import { server } from '../../test/server'
import OrderPage from './OrderPage'

const orderId = '00000000-0000-0000-0000-0000000000f1'

function openOrder() {
  server.use(signedInAs())
  return renderRoute('/orders/:id', <OrderPage pollMs={20} maxPolls={4} />, `/orders/${orderId}`)
}

const confirmed = () => anOrderSummary(orderId, { statusName: 'Confirmed', paymentStatus: 'Succeeded' })
const cancelledOrder = () => anOrderSummary(orderId, { statusName: 'Cancelled', paymentStatus: 'Refunded' })

async function askToCancel(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole('button', { name: 'Cancel order' }))
}

describe('Cancelling an order', () => {
  it.each(['Pending', 'Confirmed'])('is offered for a %s order', async (statusName) => {
    theOrderMovesThrough(orderId, [anOrderSummary(orderId, { statusName })])
    openOrder()

    expect(await screen.findByRole('button', { name: 'Cancel order' })).toBeInTheDocument()
  })

  it.each(['Cancelled', 'PaymentFailed', 'Shipped', 'Delivered'])('is not offered for a %s order', async (statusName) => {
    theOrderMovesThrough(orderId, [anOrderSummary(orderId, { statusName })])
    openOrder()

    await screen.findByText('Order')
    expect(screen.queryByRole('button', { name: 'Cancel order' })).not.toBeInTheDocument()
  })

  it('asks first, and cancels nothing when the customer keeps the order', async () => {
    const user = userEvent.setup()
    theOrderMovesThrough(orderId, [confirmed()])
    const sent = cancelApiAnswers(orderId, cancelled)
    const { container } = openOrder()

    await askToCancel(user)

    expect(screen.getByText('Cancel this order?')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Keep the order' })).toHaveFocus()
    await expectNoAccessibilityViolations(container)

    await user.click(screen.getByRole('button', { name: 'Keep the order' }))

    expect(screen.getByRole('button', { name: 'Cancel order' })).toBeInTheDocument()
    expect(sent).toHaveLength(0)
  })

  it('cancels at once when the shop says it is done (204)', async () => {
    const user = userEvent.setup()
    theOrderMovesThrough(orderId, [confirmed(), cancelledOrder()])
    const sent = cancelApiAnswers(orderId, cancelled)
    openOrder()

    await askToCancel(user)
    await user.click(screen.getByRole('button', { name: 'Yes, cancel the order' }))

    expect(await screen.findByText('Your order was cancelled.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Cancel order' })).not.toBeInTheDocument()
    expect(sent).toEqual([{ requestedWith: 'ShopEasy.Web' }]) // the header the gateway demands
  })

  it('keeps looking while the shop is cancelling (202), until the order says Cancelled', async () => {
    const user = userEvent.setup()
    // the page's first look, then the look-outs: still Confirmed twice, then Cancelled
    const looks = theOrderMovesThrough(orderId, [confirmed(), confirmed(), confirmed(), cancelledOrder()])
    cancelApiAnswers(orderId, cancelling)
    openOrder()

    await askToCancel(user)
    await user.click(screen.getByRole('button', { name: 'Yes, cancel the order' }))

    expect(await screen.findByText('We are cancelling your order.')).toBeInTheDocument()
    expect(await screen.findByText('Your order was cancelled.')).toBeInTheDocument()
    expect(screen.queryByText('We are cancelling your order.')).not.toBeInTheDocument()
    expect(looks.looks()).toBe(4)
  })

  it('stops looking after the last look, says it was accepted, and lets the customer look again', async () => {
    const user = userEvent.setup()
    const looks = theOrderMovesThrough(orderId, [
      confirmed(), // the page
      confirmed(),
      confirmed(),
      confirmed(),
      confirmed(), // the last of 4 look-outs
      cancelledOrder(),
    ])
    cancelApiAnswers(orderId, cancelling)
    openOrder()

    await askToCancel(user)
    await user.click(screen.getByRole('button', { name: 'Yes, cancel the order' }))

    expect(await screen.findByText('Your cancellation is taking longer than usual.')).toBeInTheDocument()
    expect(looks.looks()).toBe(5)

    await user.click(screen.getByRole('button', { name: 'Refresh' }))

    expect(await screen.findByText('Your order was cancelled.')).toBeInTheDocument()
  })

  it('shows the reason when it is too late (409), and offers no second try', async () => {
    const user = userEvent.setup()
    theOrderMovesThrough(orderId, [confirmed()])
    cancelApiAnswers(orderId, () => HttpResponse.json({ message: 'The order has already shipped.' }, { status: 409 }))
    openOrder()

    await askToCancel(user)
    await user.click(screen.getByRole('button', { name: 'Yes, cancel the order' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The order has already shipped.')
    expect(screen.getByText('Your order is confirmed.')).toBeInTheDocument()
  })

  it('says so when the order is not there (404)', async () => {
    const user = userEvent.setup()
    theOrderMovesThrough(orderId, [confirmed()])
    cancelApiAnswers(orderId, () => new HttpResponse(null, { status: 404 }))
    openOrder()

    await askToCancel(user)
    await user.click(screen.getByRole('button', { name: 'Yes, cancel the order' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('This order could not be found.')
  })

  it('says how long to wait when going too fast (429)', async () => {
    const user = userEvent.setup()
    theOrderMovesThrough(orderId, [confirmed()])
    cancelApiAnswers(orderId, () => HttpResponse.json({ error: 'rate_limited' }, { status: 429, headers: { 'Retry-After': '12' } }))
    openOrder()

    await askToCancel(user)
    await user.click(screen.getByRole('button', { name: 'Yes, cancel the order' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Please wait 12 seconds, then try again.')
  })

  it('lets the customer try again after a failure of the shop, and nothing is lost', async () => {
    const user = userEvent.setup()
    theOrderMovesThrough(orderId, [confirmed(), cancelledOrder()])
    const sent = cancelApiAnswers(orderId, (attempt) =>
      attempt === 1 ? HttpResponse.json({ message: 'Down.' }, { status: 503 }) : cancelled(),
    )
    openOrder()

    await askToCancel(user)
    await user.click(screen.getByRole('button', { name: 'Yes, cancel the order' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('We could not cancel the order. Nothing was changed. Try again.')

    await user.click(screen.getByRole('button', { name: 'Yes, cancel the order' }))

    expect(await screen.findByText('Your order was cancelled.')).toBeInTheDocument()
    expect(sent).toHaveLength(2)
  })

  it('shows the order as cancelled in My orders afterwards', async () => {
    const user = userEvent.setup()
    theOrderMovesThrough(orderId, [confirmed(), cancelledOrder()])
    cancelApiAnswers(orderId, cancelled)
    theOrdersAre([anOrderSummary(orderId, { statusName: 'Confirmed' })])
    server.use(signedInAs())
    renderApp(`/orders/${orderId}`)

    await askToCancel(user)
    await user.click(screen.getByRole('button', { name: 'Yes, cancel the order' }))
    await screen.findByText('Your order was cancelled.')
    theOrdersAre([anOrderSummary(orderId, { statusName: 'Cancelled' })]) // what the shop lists now
    await user.click(screen.getByRole('link', { name: 'Orders' }))

    expect(await screen.findByText('Cancelled')).toBeInTheDocument()
  })
})
