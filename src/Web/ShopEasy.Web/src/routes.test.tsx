import { render, screen } from '@testing-library/react'
import { createMemoryRouter, type RouteObject } from 'react-router'
import { RouterProvider } from 'react-router/dom'
import { describe, expect, it, vi } from 'vitest'
import RouteErrorPage from './pages/RouteErrorPage'
import { expectNoAccessibilityViolations } from './test/a11y'
import { catalogHas, headphones } from './test/catalog'
import { nobodyIsSignedIn } from './test/gateway'
import { renderApp } from './test/render'
import { server } from './test/server'

describe('routes', () => {
  it('opens the product list at the root address', async () => {
    server.use(nobodyIsSignedIn())
    catalogHas([headphones])

    const { router } = renderApp('/')

    expect(await screen.findByRole('heading', { name: 'Products' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/products')
  })

  it('has the shell around every page: skip link, banner with sign-in, main', async () => {
    server.use(nobodyIsSignedIn())
    catalogHas([headphones])

    const { container } = renderApp('/products')

    expect(await screen.findByRole('link', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Skip to content' })).toHaveAttribute('href', '#content')
    expect(screen.getByRole('link', { name: 'ShopEasy' })).toHaveAttribute('href', '/')
    expect(screen.getByRole('main')).toBeInTheDocument()
    await expectNoAccessibilityViolations(container)
  })

  it('says so for an address that leads nowhere', async () => {
    server.use(nobodyIsSignedIn())

    renderApp('/nowhere/at/all')

    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Back to the products' })).toHaveAttribute('href', '/products')
  })
})

describe('RouteErrorPage', () => {
  function renderBrokenRoute(route: RouteObject) {
    const router = createMemoryRouter([{ path: '/', errorElement: <RouteErrorPage />, ...route }], {
      initialEntries: ['/'],
    })
    render(<RouterProvider router={router} />)
  }

  it('shows a calm message when a page breaks, and keeps the details for the console', () => {
    const logged = vi.spyOn(console, 'error').mockImplementation(() => undefined)

    renderBrokenRoute({
      Component: () => {
        throw new Error('boom')
      },
    })

    expect(screen.getByRole('alert')).toHaveTextContent('Something went wrong')
    expect(screen.queryByText('boom')).not.toBeInTheDocument()
    expect(logged).toHaveBeenCalled()
    logged.mockRestore()
  })

  it('shows the not-found page for a 404 answer', async () => {
    renderBrokenRoute({
      loader: () => {
        // eslint-disable-next-line @typescript-eslint/only-throw-error -- a router answer, as React Router documents it
        throw new Response('', { status: 404 })
      },
      Component: () => null,
    })

    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeInTheDocument()
  })
})
