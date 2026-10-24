import { act, screen } from '@testing-library/react'
import { delay, http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { expectNoAccessibilityViolations } from '../test/a11y'
import { aProduct } from '../test/catalog'
import { alice, nobodyIsSignedIn, signedInAs } from '../test/gateway'
import { renderWithProviders } from '../test/render'
import { cartStore } from '../features/cart/cartStore'
import { server } from '../test/server'
import Header from './Header'

describe('Header', () => {
  it('offers to sign in when nobody is, and comes back to this page', async () => {
    server.use(nobodyIsSignedIn())

    const { container } = renderWithProviders(<Header />)

    expect(await screen.findByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/account/login?returnUrl=%2F')
    expect(screen.queryByRole('link', { name: 'Sign out' })).not.toBeInTheDocument()
    await expectNoAccessibilityViolations(container)
  })

  it('comes back to the page with its filters, as the router knows it', async () => {
    server.use(nobodyIsSignedIn())

    renderWithProviders(<Header />, '/products?q=desk&page=2')

    expect(await screen.findByRole('link', { name: 'Sign in' })).toHaveAttribute(
      'href',
      '/account/login?returnUrl=%2Fproducts%3Fq%3Ddesk%26page%3D2',
    )
  })

  it('links to the cart, with the number of pieces in it', async () => {
    server.use(nobodyIsSignedIn())
    cartStore.add(aProduct(1).id)
    cartStore.add(aProduct(1).id)
    cartStore.add(aProduct(2).id)

    renderWithProviders(<Header />)

    expect(await screen.findByRole('link', { name: 'Cart, 3 items' })).toHaveAttribute('href', '/cart')
  })

  it('follows the cart as it changes', async () => {
    server.use(nobodyIsSignedIn())
    renderWithProviders(<Header />)
    expect(await screen.findByRole('link', { name: 'Cart, 0 items' })).toBeInTheDocument()

    act(() => { cartStore.add(aProduct(1).id) })

    expect(screen.getByRole('link', { name: 'Cart, 1 item' })).toBeInTheDocument()
  })

  it('shows who is signed in, with a way to sign out', async () => {
    server.use(signedInAs())

    const { container } = renderWithProviders(<Header />)

    expect(await screen.findByText('Alice')).toBeInTheDocument()
    await expectNoAccessibilityViolations(container)
    expect(screen.getByRole('link', { name: 'Sign out' })).toHaveAttribute('href', '/account/logout')
    expect(screen.queryByRole('link', { name: 'Sign in' })).not.toBeInTheDocument()
  })

  it('links a signed-in customer to their orders, and a visitor to nothing of the kind', async () => {
    server.use(signedInAs())
    const { unmount } = renderWithProviders(<Header />)
    expect(await screen.findByRole('link', { name: 'Orders' })).toHaveAttribute('href', '/orders')
    unmount()

    server.use(nobodyIsSignedIn())
    renderWithProviders(<Header />)
    await screen.findByRole('link', { name: 'Sign in' })
    expect(screen.queryByRole('link', { name: 'Orders' })).not.toBeInTheDocument()
  })

  it('shows the email of an account that has no name', async () => {
    server.use(signedInAs({ ...alice, name: null }))

    renderWithProviders(<Header />)

    expect(await screen.findByText('alice@example.com')).toBeInTheDocument()
  })

  it('still greets an account that has neither name nor email', async () => {
    server.use(signedInAs({ ...alice, name: null, email: null }))

    renderWithProviders(<Header />)

    expect(await screen.findByText('Your account')).toBeInTheDocument()
  })

  it('says so when the gateway answers with something unexpected', async () => {
    server.use(signedInAs({ hello: 'world' }))

    renderWithProviders(<Header />)

    expect(await screen.findByText('Your account is unavailable.')).toBeInTheDocument()
  })

  it('shows neither link while it is still finding out', () => {
    server.use(
      http.get('/account/me', async () => {
        await delay('infinite')
        return HttpResponse.json(alice)
      }),
    )

    renderWithProviders(<Header />)

    expect(screen.getByRole('navigation', { name: 'Account' })).toHaveAttribute('aria-busy', 'true')
    expect(screen.queryByRole('link', { name: /sign/i })).not.toBeInTheDocument()
  })

  it('says so when the account cannot be looked up', async () => {
    server.use(http.get('/account/me', () => HttpResponse.json({ message: 'Down.' }, { status: 503 })))

    renderWithProviders(<Header />)

    expect(await screen.findByText('Your account is unavailable.')).toBeInTheDocument()
  })
})
