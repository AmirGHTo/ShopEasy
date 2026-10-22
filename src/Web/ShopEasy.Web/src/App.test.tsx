import { screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import App from './App'
import { expectNoAccessibilityViolations } from './test/a11y'
import { nobodyIsSignedIn } from './test/gateway'
import { renderWithProviders } from './test/render'
import { server } from './test/server'

describe('App', () => {
  it('shows the shop name as a link home, and the sign-in', async () => {
    server.use(nobodyIsSignedIn())

    const { container } = renderWithProviders(<App />)

    expect(screen.getByRole('link', { name: 'ShopEasy' })).toHaveAttribute('href', '/')
    expect(await screen.findByRole('link', { name: 'Sign in' })).toBeInTheDocument()
    await expectNoAccessibilityViolations(container)
  })
})
