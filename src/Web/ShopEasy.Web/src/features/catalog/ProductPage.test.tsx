import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { expectNoAccessibilityViolations } from '../../test/a11y'
import { aProduct, catalogHas, desk, headphones } from '../../test/catalog'
import { nobodyIsSignedIn } from '../../test/gateway'
import { renderApp } from '../../test/render'
import { server } from '../../test/server'

describe('ProductPage', () => {
  it('shows the product: name, category, price, description', async () => {
    server.use(nobodyIsSignedIn())
    catalogHas([headphones, desk])

    const { container } = renderApp(`/products/${headphones.id}`)

    expect(await screen.findByRole('heading', { name: 'Wireless Headphones', level: 1 })).toBeInTheDocument()
    expect(screen.getByText('$299.99')).toBeInTheDocument()
    expect(screen.getByText('Electronics')).toBeInTheDocument()
    expect(screen.getByText('Premium noise-cancelling headphones')).toBeInTheDocument()
    expect(screen.queryByText('This product is no longer available.')).not.toBeInTheDocument()
    expect(screen.getByRole('link', { name: '← All products' })).toHaveAttribute('href', '/products')
    expect(document.title).toBe('Wireless Headphones · ShopEasy')
    await expectNoAccessibilityViolations(container)
  })

  it('says that a product the shop no longer sells is not available', async () => {
    server.use(nobodyIsSignedIn())
    catalogHas([aProduct(7, { name: 'Old Radio', isActive: false })])

    renderApp(`/products/${aProduct(7).id}`)

    expect(await screen.findByRole('heading', { name: 'Old Radio' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent('This product is no longer available.')
  })

  it('says so for a product that does not exist', async () => {
    server.use(nobodyIsSignedIn())
    catalogHas([headphones])

    renderApp(`/products/${aProduct(99).id}`)

    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeInTheDocument()
    expect(screen.getByText('There is no such product.')).toBeInTheDocument()
  })

  it('does not even ask the API when the id is not a UUID', async () => {
    let asked = false
    server.use(
      nobodyIsSignedIn(),
      http.get('/api/v1/products/:id', () => {
        asked = true
        return new HttpResponse(null, { status: 404 })
      }),
    )

    renderApp('/products/..%2F..%2Fadmin')

    expect(await screen.findByRole('heading', { name: 'Page not found' })).toBeInTheDocument()
    expect(asked).toBe(false)
  })

  it('says so when the product cannot be loaded, and tries again', async () => {
    const user = userEvent.setup()
    server.use(
      nobodyIsSignedIn(),
      http.get('/api/v1/products/:id', () => HttpResponse.json({ message: 'Down.' }, { status: 503 })),
    )
    renderApp(`/products/${headphones.id}`)
    expect(await screen.findByRole('alert')).toHaveTextContent('We could not load this product.')

    catalogHas([headphones]) // the service is back
    await user.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByRole('heading', { name: 'Wireless Headphones' })).toBeInTheDocument()
  })

  it('is reached from the list, and goes back to it', async () => {
    const user = userEvent.setup()
    server.use(nobodyIsSignedIn())
    catalogHas([headphones, desk])
    renderApp('/products')

    await user.click(await screen.findByRole('link', { name: 'Standing Desk' }))
    expect(await screen.findByRole('heading', { name: 'Standing Desk', level: 1 })).toBeInTheDocument()

    await user.click(screen.getByRole('link', { name: '← All products' }))
    expect(await screen.findByRole('heading', { name: 'Products' })).toBeInTheDocument()
  })
})
