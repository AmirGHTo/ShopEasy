import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { expectNoAccessibilityViolations } from '../../test/a11y'
import { aProduct, catalogHas, desk, headphones, lamp } from '../../test/catalog'
import { nobodyIsSignedIn } from '../../test/gateway'
import { renderApp } from '../../test/render'
import { server } from '../../test/server'
import { cartStore, STORAGE_KEY } from './cartStore'

function openCart(products = [headphones, desk, lamp]) {
  server.use(nobodyIsSignedIn())
  catalogHas(products)
  return renderApp('/cart')
}

// the cart line that holds the link of this product
function lineOf(name: string) {
  const item = screen.getAllByRole('listitem').find((li) => within(li).queryByRole('link', { name }))
  if (!item) throw new Error(`no cart line for ${name}`)
  return item
}

describe('CartPage', () => {
  it('says so when the cart is empty', async () => {
    const { container } = openCart()

    expect(await screen.findByText('Your cart is empty.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Browse the products' })).toHaveAttribute('href', '/products')
    await expectNoAccessibilityViolations(container)
  })

  it('lists the lines with their prices, line totals and the subtotal', async () => {
    cartStore.add(headphones.id)
    cartStore.add(headphones.id)
    cartStore.add(lamp.id)
    const { container } = openCart()

    expect(await screen.findByRole('link', { name: 'Wireless Headphones' })).toHaveAttribute('href', `/products/${headphones.id}`)
    const headphonesLine = within(lineOf('Wireless Headphones'))
    expect(headphonesLine.getByText('$299.99 each')).toBeInTheDocument()
    expect(headphonesLine.getByRole('spinbutton', { name: 'Quantity of Wireless Headphones' })).toHaveValue(2)
    expect(headphonesLine.getByText('$599.98')).toBeInTheDocument()
    expect(within(lineOf('Desk Lamp')).getByText('$39.50')).toBeInTheDocument()
    expect(screen.getByText(/Subtotal \(3 items\)/)).toHaveTextContent('$639.48')
    await expectNoAccessibilityViolations(container)
  })

  it('changes a quantity as it is typed, and the totals follow', async () => {
    const user = userEvent.setup()
    cartStore.add(lamp.id)
    openCart()
    const field = await screen.findByRole('spinbutton', { name: 'Quantity of Desk Lamp' })

    await user.clear(field)
    await user.type(field, '12')

    expect(field).toHaveValue(12)
    expect(cartStore.getSnapshot()).toEqual([{ productId: lamp.id, quantity: 12 }])
    expect(screen.getAllByText('$474.00')).toHaveLength(2) // the line and the subtotal
  })

  it('shows the most that can be ordered when more is typed', async () => {
    const user = userEvent.setup()
    cartStore.add(lamp.id)
    openCart()
    const field = await screen.findByRole('spinbutton', { name: 'Quantity of Desk Lamp' })

    await user.clear(field)
    await user.type(field, '150')

    expect(field).toHaveValue(100)
    expect(cartStore.getSnapshot()).toEqual([{ productId: lamp.id, quantity: 100 }])
  })

  it('leaves a half-written number alone while typing, and puts the quantity back when the field is left', async () => {
    const user = userEvent.setup()
    cartStore.add(lamp.id)
    cartStore.setQuantity(lamp.id, 3)
    openCart()
    const field = await screen.findByRole('spinbutton', { name: 'Quantity of Desk Lamp' })

    await user.clear(field)
    expect(field).toHaveValue(null)
    await user.type(field, '0')
    expect(field).toHaveValue(0)
    expect(cartStore.getSnapshot()).toEqual([{ productId: lamp.id, quantity: 3 }])

    await user.tab()
    expect(field).toHaveValue(3)
  })

  it('removes a line, and says the cart is empty after the last one', async () => {
    const user = userEvent.setup()
    cartStore.add(headphones.id)
    cartStore.add(lamp.id)
    openCart()
    await screen.findByRole('link', { name: 'Desk Lamp' })

    await user.click(screen.getByRole('button', { name: 'Remove Desk Lamp' }))
    expect(screen.queryByRole('link', { name: 'Desk Lamp' })).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Wireless Headphones' })).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Remove Wireless Headphones' }))
    expect(await screen.findByText('Your cart is empty.')).toBeInTheDocument()
    expect(localStorage.getItem(STORAGE_KEY)).toBe('[]')
  })

  it('leads on to the checkout when there is something to buy', async () => {
    cartStore.add(lamp.id)
    openCart()

    expect(await screen.findByRole('link', { name: 'Checkout' })).toHaveAttribute('href', '/checkout')
  })

  it('offers no checkout when nothing in the cart can be bought', async () => {
    const old = aProduct(7, { name: 'Old Radio', isActive: false })
    cartStore.add(old.id)
    openCart([old])

    await screen.findByText('No longer available')

    expect(screen.queryByRole('link', { name: 'Checkout' })).not.toBeInTheDocument()
  })

  it('keeps a product that is no longer sold out of the total, and lets it be removed', async () => {
    const user = userEvent.setup()
    const old = aProduct(7, { name: 'Old Radio', price: 50, isActive: false })
    cartStore.add(old.id)
    cartStore.add(lamp.id)
    openCart([old, lamp])

    expect(await screen.findByText('Old Radio')).toBeInTheDocument()
    expect(screen.getByText('No longer available')).toBeInTheDocument()
    expect(screen.getByText(/Subtotal \(1 items\)/)).toHaveTextContent('$39.50')
    expect(screen.getByText('Products that are no longer available are not counted.')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Remove Old Radio' }))
    expect(screen.queryByText('Old Radio')).not.toBeInTheDocument()
  })

  it('says so for a product that is gone from the catalog altogether', async () => {
    cartStore.add(aProduct(99).id)
    openCart([lamp])

    expect(await screen.findByText('A product that no longer exists')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Remove the unavailable product' })).toBeInTheDocument()
  })

  it('says so when a product cannot be loaded, and tries again', async () => {
    const user = userEvent.setup()
    cartStore.add(lamp.id)
    server.use(
      nobodyIsSignedIn(),
      http.get('/api/v1/products/:id', () => HttpResponse.json({ message: 'Down.' }, { status: 503 })),
    )
    renderApp('/cart')
    expect(await screen.findByRole('alert')).toHaveTextContent('We could not load a product in your cart.')

    catalogHas([lamp]) // the service is back
    await user.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByRole('link', { name: 'Desk Lamp' })).toBeInTheDocument()
  })

  it('is the same cart after a reload', async () => {
    cartStore.add(desk.id)
    cartStore.setQuantity(desk.id, 2)
    const view = openCart()
    expect(await screen.findByRole('spinbutton', { name: 'Quantity of Standing Desk' })).toHaveValue(2)
    view.unmount()

    openCart()

    expect(await screen.findByRole('spinbutton', { name: 'Quantity of Standing Desk' })).toHaveValue(2)
  })
})
