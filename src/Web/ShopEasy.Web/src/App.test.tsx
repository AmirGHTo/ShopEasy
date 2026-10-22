import { screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import App from './App'
import { stubFetch } from './test/fetch'
import { renderWithProviders } from './test/render'

describe('App', () => {
  it('shows the shop name as a link home, and the sign-in', async () => {
    stubFetch(new Response(null, { status: 401 }))

    renderWithProviders(<App />)

    expect(screen.getByRole('link', { name: 'ShopEasy' })).toHaveAttribute('href', '/')
    expect(await screen.findByRole('link', { name: 'Sign in' })).toBeInTheDocument()
  })
})
