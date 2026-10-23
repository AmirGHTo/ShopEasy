import { act, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { delay, http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { expectNoAccessibilityViolations } from '../../test/a11y'
import { aProduct, catalogHas, desk, headphones, lamp } from '../../test/catalog'
import { nobodyIsSignedIn, signedInAs } from '../../test/gateway'
import { aPlacedOrder, created, orderApiAnswers } from '../../test/orders'
import { renderApp } from '../../test/render'
import { server } from '../../test/server'
import { cartStore } from '../cart/cartStore'

const orderId = '00000000-0000-0000-0000-0000000000f1'

function openCheckout(products = [headphones, desk, lamp]) {
  server.use(signedInAs())
  catalogHas(products)
  return renderApp('/checkout')
}

// a cart of a desk and two lamps: 599.99 + 2 × 39.50 = 678.99
function fillTheCart() {
  cartStore.add(desk.id)
  cartStore.add(lamp.id)
  cartStore.add(lamp.id)
}

// the button, once the prices have been checked and it can be pressed
async function placeOrderButton() {
  const button = await screen.findByRole('button', { name: 'Place order' })
  await waitFor(() => { expect(button).toBeEnabled() })
  return button
}

describe('CheckoutPage', () => {
  it('asks a visitor to sign in, and comes back here after it', async () => {
    fillTheCart()
    server.use(nobodyIsSignedIn())
    catalogHas([desk, lamp])
    renderApp('/checkout')

    expect(await screen.findByText('Sign in to place your order. Your cart is kept.')).toBeInTheDocument()
    expect(within(screen.getByRole('main')).getByRole('link', { name: 'Sign in' })).toHaveAttribute(
      'href',
      '/account/login?returnUrl=%2Fcheckout',
    )
    expect(screen.queryByRole('button', { name: 'Place order' })).not.toBeInTheDocument()
  })

  it('says so when the cart is empty', async () => {
    openCheckout()

    expect(await screen.findByText('Your cart is empty.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Browse the products' })).toHaveAttribute('href', '/products')
  })

  it('shows what is being ordered and an estimate of the total', async () => {
    fillTheCart()
    const { container } = openCheckout()

    expect(await placeOrderButton()).toBeEnabled()
    expect(screen.getByText(/Standing Desk/)).toBeInTheDocument()
    expect(screen.getByText('$599.99')).toBeInTheDocument()
    expect(screen.getByText('$79.00')).toBeInTheDocument() // two lamps
    expect(screen.getByText(/Estimated total/)).toHaveTextContent('$678.99')
    expect(screen.getByText('The shop confirms the prices when the order is placed.')).toBeInTheDocument()
    await expectNoAccessibilityViolations(container)
  })

  it('places the order with a key, empties the cart and goes to the order', async () => {
    const user = userEvent.setup()
    fillTheCart()
    const sent = orderApiAnswers(() => created(aPlacedOrder(orderId, 678.99)))
    const { router } = openCheckout()

    await user.click(await placeOrderButton())

    await waitFor(() => { expect(router.state.location.pathname).toBe(`/orders/${orderId}`) })
    expect(sent).toHaveLength(1)
    expect(sent[0]?.body).toEqual({
      items: [
        { productId: desk.id, quantity: 1 },
        { productId: lamp.id, quantity: 2 },
      ],
    })
    expect(sent[0]?.idempotencyKey).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
    expect(cartStore.getSnapshot()).toEqual([])
    expect(sessionStorage.getItem('shopeasy.checkout.v1')).toBeNull() // the next order gets a new key
  })

  it('sends the note, trimmed', async () => {
    const user = userEvent.setup()
    fillTheCart()
    const sent = orderApiAnswers(() => created(aPlacedOrder(orderId)))
    const { router } = openCheckout()

    await user.type(await screen.findByRole('textbox', { name: /Notes for the order/ }), '  leave it at the door  ')
    await user.click(await placeOrderButton())

    await waitFor(() => { expect(router.state.location.pathname).toBe(`/orders/${orderId}`) })
    expect(sent[0]?.body.notes).toBe('leave it at the door')
  })

  it('shows what the shop said was wrong, and keeps the cart', async () => {
    const user = userEvent.setup()
    fillTheCart()
    const refusal = { message: 'Order could not be placed.', errors: ['Item 1: not enough stock for Standing Desk.'] }
    orderApiAnswers(() => HttpResponse.json(refusal, { status: 400 }))
    openCheckout()

    await user.click(await placeOrderButton())

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Your order could not be placed.')
    expect(alert).toHaveTextContent('Item 1: not enough stock for Standing Desk.')
    expect(cartStore.getSnapshot()).toHaveLength(2)
    expect(screen.getByRole('button', { name: 'Place order' })).toBeEnabled()
  })

  it('uses the same key for the same order, and a new one when the order changes', async () => {
    const user = userEvent.setup()
    fillTheCart()
    const sent = orderApiAnswers(() => HttpResponse.json({ message: 'No.', errors: ['No.'] }, { status: 400 }))
    openCheckout()

    await user.click(await placeOrderButton())
    await screen.findByRole('alert')
    await user.click(screen.getByRole('button', { name: 'Place order' }))
    await waitFor(() => { expect(sent).toHaveLength(2) })
    await user.type(screen.getByRole('textbox', { name: /Notes for the order/ }), 'a note')
    await user.click(screen.getByRole('button', { name: 'Place order' }))
    await waitFor(() => { expect(sent).toHaveLength(3) })

    expect(sent[1]?.idempotencyKey).toBe(sent[0]?.idempotencyKey)
    expect(sent[2]?.idempotencyKey).not.toBe(sent[0]?.idempotencyKey)
  })

  it('tells how long to wait when orders come too fast', async () => {
    const user = userEvent.setup()
    fillTheCart()
    orderApiAnswers(() =>
      HttpResponse.json({ error: 'Too many requests', retryAfter: 30 }, { status: 429, headers: { 'Retry-After': '30' } }),
    )
    openCheckout()

    await user.click(await placeOrderButton())

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('You are placing orders too quickly.')
    expect(alert).toHaveTextContent('Please wait 30 seconds')
  })

  it('says that nothing was ordered when the shop could not check the prices', async () => {
    const user = userEvent.setup()
    fillTheCart()
    orderApiAnswers(() => HttpResponse.json({ message: 'Down.' }, { status: 503, headers: { 'Retry-After': '5' } }))
    openCheckout()

    await user.click(await placeOrderButton())

    expect(await screen.findByRole('alert')).toHaveTextContent('Nothing was ordered.')
  })

  it('after a lost answer, placing the order again cannot order twice: it is the same key', async () => {
    const user = userEvent.setup()
    fillTheCart()
    const sent = orderApiAnswers((attempt) => (attempt === 1 ? HttpResponse.error() : created(aPlacedOrder(orderId))))
    const { router } = openCheckout()

    await user.click(await placeOrderButton())
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('We could not confirm that your order was placed.')
    expect(alert).toHaveTextContent('it will not be ordered twice')

    await user.click(screen.getByRole('button', { name: 'Place order' }))

    await waitFor(() => { expect(router.state.location.pathname).toBe(`/orders/${orderId}`) })
    expect(sent).toHaveLength(2)
    expect(sent[0]?.idempotencyKey).toBeTruthy()
    expect(sent[1]?.idempotencyKey).toBe(sent[0]?.idempotencyKey)
  })

  it('asks to sign in again when the session ended', async () => {
    const user = userEvent.setup()
    fillTheCart()
    orderApiAnswers(() => {
      server.use(nobodyIsSignedIn()) // from now on the gateway knows nobody
      return HttpResponse.json({ error: 'not_signed_in' }, { status: 401 })
    })
    openCheckout()

    await user.click(await placeOrderButton())

    expect(await screen.findByText('Sign in to place your order. Your cart is kept.')).toBeInTheDocument()
    expect(cartStore.getSnapshot()).toHaveLength(2)
  })

  it('sends one order for a double click', async () => {
    const user = userEvent.setup()
    fillTheCart()
    const sent = orderApiAnswers(async () => {
      await delay(100)
      return created(aPlacedOrder(orderId))
    })
    const { router } = openCheckout()

    await user.dblClick(await placeOrderButton())

    await waitFor(() => { expect(router.state.location.pathname).toBe(`/orders/${orderId}`) })
    expect(sent).toHaveLength(1)
  })

  it('shows that the order is being placed, and cannot be pressed again meanwhile', async () => {
    const user = userEvent.setup()
    fillTheCart()
    orderApiAnswers(async () => {
      await delay(300)
      return created(aPlacedOrder(orderId))
    })
    const { router } = openCheckout()

    await user.click(await placeOrderButton())

    const busy = await screen.findByRole('button', { name: 'Placing your order…' })
    expect(busy).toBeDisabled()

    // the order still completes (and empties the cart) after the page is gone, so wait for it: a test
    // that ended first would have its late answer empty the cart of the next one
    await waitFor(() => { expect(router.state.location.pathname).toBe(`/orders/${orderId}`) })
  })

  it('does not place the order while the prices are still being checked', async () => {
    fillTheCart()
    server.use(nobodyIsSignedIn())
    server.use(signedInAs())
    catalogHas([desk, lamp])
    server.use(
      http.get('/api/v1/products/:id', async () => {
        await delay(200)
        return HttpResponse.json(desk)
      }),
    )
    renderApp('/checkout')

    expect(await screen.findByText('Checking your cart…')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Place order' })).toBeDisabled()
    expect(await placeOrderButton()).toBeEnabled() // and once they are known, it can be pressed
  })

  it('does not place an order that holds a product that is no longer sold, until it is removed', async () => {
    const old = aProduct(7, { name: 'Old Radio', isActive: false })
    cartStore.add(lamp.id)
    cartStore.add(old.id)
    openCheckout([lamp, old])

    expect(await screen.findByText(/no longer available. Remove them to continue/)).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Place order' })).toBeDisabled()

    act(() => { cartStore.remove(old.id) })

    expect(await screen.findByRole('button', { name: 'Place order' })).toBeEnabled()
  })
})



