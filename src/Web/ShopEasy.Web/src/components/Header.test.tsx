import { screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { jsonResponse, stubFetch } from '../test/fetch'
import { renderWithProviders } from '../test/render'
import Header from './Header'

const alice = { isAuthenticated: true, userId: 'u-1', email: 'alice@example.com', name: 'Alice', roles: ['customer'] }

describe('Header', () => {
  it('offers to sign in when nobody is, and comes back to this page', async () => {
    stubFetch(new Response(null, { status: 401 }))

    renderWithProviders(<Header />)

    expect(await screen.findByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/account/login?returnUrl=%2F')
    expect(screen.queryByRole('link', { name: 'Sign out' })).not.toBeInTheDocument()
  })

  it('shows who is signed in, with a way to sign out', async () => {
    stubFetch(jsonResponse(alice))

    renderWithProviders(<Header />)

    expect(await screen.findByText('Alice')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Sign out' })).toHaveAttribute('href', '/account/logout')
    expect(screen.queryByRole('link', { name: 'Sign in' })).not.toBeInTheDocument()
  })

  it('shows the email of an account that has no name', async () => {
    stubFetch(jsonResponse({ ...alice, name: null }))

    renderWithProviders(<Header />)

    expect(await screen.findByText('alice@example.com')).toBeInTheDocument()
  })

  it('shows neither link while it is still finding out', () => {
    stubFetch().mockReturnValue(new Promise<Response>(() => {}))

    renderWithProviders(<Header />)

    expect(screen.getByRole('navigation', { name: 'Account' })).toHaveAttribute('aria-busy', 'true')
    expect(screen.queryByRole('link', { name: /sign/i })).not.toBeInTheDocument()
  })

  it('says so when the account cannot be looked up', async () => {
    stubFetch(jsonResponse({ message: 'Down.' }, 503))

    renderWithProviders(<Header />)

    expect(await screen.findByText('Your account is unavailable.')).toBeInTheDocument()
  })
})
