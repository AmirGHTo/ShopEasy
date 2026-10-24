import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { expectNoAccessibilityViolations } from '../../test/a11y'
import { signedInAs } from '../../test/gateway'
import { anOrderSummary, theOrdersAre } from '../../test/orders'
import { renderApp } from '../../test/render'
import { server } from '../../test/server'

const id = (n: number) => `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`

const placedAt = (n: number) => `2026-10-${String(n).padStart(2, '0')}T10:00:00Z`

const confirmed = anOrderSummary(id(1), { statusName: 'Confirmed', placedAt: placedAt(20), amountDisplay: '$100.00', itemsSummary: 'Desk x1' })
const pending = anOrderSummary(id(2), { statusName: 'Pending', placedAt: placedAt(21), amountDisplay: '$39.50', itemsSummary: 'Desk Lamp x1' })
const cancelled = anOrderSummary(id(3), { statusName: 'Cancelled', placedAt: placedAt(19), amountDisplay: '$599.99', itemsSummary: 'Standing Desk x1' })

function openOrders(orders = [confirmed, pending, cancelled], address = '/orders') {
  server.use(signedInAs())
  const requests = theOrdersAre(orders)
  return { requests, ...renderApp(address) }
}

function itemAt(items: readonly HTMLElement[], index: number): HTMLElement {
  const item = items[index]
  if (!item) throw new Error(`there is no list item ${String(index)}`)
  return item
}

describe('OrdersPage', () => {
  it('lists the orders, newest first, each linking to its page', async () => {
    const { container } = openOrders()

    const list = await screen.findByRole('list')
    const items = within(list).getAllByRole('listitem')

    expect(items).toHaveLength(3)
    expect(within(itemAt(items, 0)).getByText('Desk Lamp x1')).toBeInTheDocument() // the 21st
    expect(within(itemAt(items, 0)).getByText('Pending')).toBeInTheDocument()
    expect(within(itemAt(items, 0)).getByText('$39.50')).toBeInTheDocument()
    expect(within(itemAt(items, 0)).getByRole('link')).toHaveAttribute('href', `/orders/${id(2)}`)
    expect(within(itemAt(items, 2)).getByText('Cancelled')).toBeInTheDocument()
    await expectNoAccessibilityViolations(container)
  })

  it('narrows the list by status, and puts it in the address', async () => {
    const user = userEvent.setup()
    const { router } = openOrders()
    await screen.findByRole('list')

    await user.selectOptions(screen.getByRole('combobox', { name: 'Show' }), 'Confirmed')

    await waitFor(() => { expect(screen.getAllByRole('listitem')).toHaveLength(1) })
    expect(screen.getByText('Desk x1')).toBeInTheDocument()
    expect(router.state.location.search).toBe('?status=Confirmed')
  })

  it('reads the filter and the page from the address', async () => {
    const many = Array.from({ length: 12 }, (_, i) => anOrderSummary(id(100 + i), { statusName: 'Confirmed', placedAt: placedAt(1 + i) }))
    const { requests } = openOrders(many, '/orders?status=Confirmed&page=2')

    await screen.findByRole('list')

    expect(requests.at(-1)?.searchParams.get('status')).toBe('Confirmed')
    expect(requests.at(-1)?.searchParams.get('page')).toBe('2')
    expect(screen.getByRole('combobox', { name: 'Show' })).toHaveValue('Confirmed')
    expect(screen.getAllByRole('listitem')).toHaveLength(2) // 12 orders, 10 a page
  })

  it('moves between pages and keeps the filter', async () => {
    const user = userEvent.setup()
    const many = Array.from({ length: 25 }, (_, i) => anOrderSummary(id(100 + i), { statusName: 'Confirmed', placedAt: placedAt(1 + (i % 28)) }))
    const { router } = openOrders(many, '/orders?status=Confirmed')
    await screen.findByText('Page 1 of 3')

    await user.click(screen.getByRole('button', { name: 'Next' }))

    await screen.findByText('Page 2 of 3')
    expect(router.state.location.search).toBe('?status=Confirmed&page=2')
  })

  it('ignores a status it does not know', async () => {
    const { requests } = openOrders([confirmed], '/orders?status=Whatever')

    await screen.findByRole('list')

    expect(requests.at(-1)?.searchParams.has('status')).toBe(false)
  })

  it('says so when there are no orders yet, and offers the products', async () => {
    openOrders([])

    expect(await screen.findByText('You have not placed any orders yet.')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Browse the products' })).toHaveAttribute('href', '/products')
  })

  it('says so when no order has the status asked for', async () => {
    openOrders([confirmed], '/orders?status=Cancelled')

    expect(await screen.findByText('No orders with this status.')).toBeInTheDocument()
  })

  it('asks a visitor to sign in', async () => {
    server.use(http.get('/api/orders', () => HttpResponse.json({ error: 'not_signed_in' }, { status: 401 })))
    renderApp('/orders')

    expect(await screen.findByText('Sign in to see your orders.')).toBeInTheDocument()
    expect(within(screen.getByRole('main')).getByRole('link', { name: 'Sign in' })).toHaveAttribute(
      'href',
      '/account/login?returnUrl=%2Forders',
    )
  })

  it('says so when the orders cannot be loaded, and tries again', async () => {
    const user = userEvent.setup()
    server.use(signedInAs(), http.get('/api/orders', () => HttpResponse.json({ message: 'Down.' }, { status: 503 })))
    renderApp('/orders')
    expect(await screen.findByRole('alert')).toHaveTextContent('We could not load your orders.')

    theOrdersAre([confirmed]) // the shop is back
    await user.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByText('Desk x1')).toBeInTheDocument()
  })

  it('shows a status it does not know as it is', async () => {
    openOrders([anOrderSummary(id(9), { statusName: 'OnTheMoon' })])

    expect(await screen.findByText('OnTheMoon')).toBeInTheDocument()
  })
})
