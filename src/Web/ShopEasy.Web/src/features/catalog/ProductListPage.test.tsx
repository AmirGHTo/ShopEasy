import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { expectNoAccessibilityViolations } from '../../test/a11y'
import { aProduct, catalogHas, desk, headphones, lamp } from '../../test/catalog'
import { nobodyIsSignedIn } from '../../test/gateway'
import { renderApp } from '../../test/render'
import { server } from '../../test/server'

const shop = [headphones, desk, lamp]

function setUp(products = shop, address = '/products') {
  server.use(nobodyIsSignedIn())
  const requests = catalogHas(products)
  return { requests, ...renderApp(address) }
}

describe('ProductListPage', () => {
  it('lists the products, each linking to its page', async () => {
    const { container } = setUp()

    const link = await screen.findByRole('link', { name: 'Wireless Headphones' })

    expect(link).toHaveAttribute('href', `/products/${headphones.id}`)
    expect(screen.getByText('$299.99')).toBeInTheDocument()
    expect(screen.getByText('Premium noise-cancelling headphones')).toBeInTheDocument()
    expect(screen.getAllByRole('listitem')).toHaveLength(3)
    await expectNoAccessibilityViolations(container)
  })

  it('shows a placeholder while the first page loads', async () => {
    setUp()

    expect(await screen.findByRole('status', { name: 'Loading products' })).toBeInTheDocument()
    expect(await screen.findByRole('link', { name: 'Desk Lamp' })).toBeInTheDocument()
    expect(screen.queryByRole('status', { name: 'Loading products' })).not.toBeInTheDocument()
  })

  it('offers the categories the catalog has', async () => {
    setUp()

    const select = await screen.findByRole('combobox', { name: 'Category' })

    await waitFor(() => { expect(within(select).getAllByRole('option')).toHaveLength(3) })
    expect(within(select).getAllByRole('option').map((o) => o.textContent)).toEqual([
      'All categories',
      'Electronics',
      'Furniture',
    ])
  })

  it('reads the filters and the page from the address', async () => {
    const { requests } = setUp([...shop, ...Array.from({ length: 24 }, (_, i) => aProduct(10 + i, { name: `Desk ${String(i)}`, category: 'Furniture' }))],
      '/products?q=desk&category=Furniture&page=2')

    await screen.findAllByRole('link', { name: /^Desk/ })

    expect(requests.at(-1)?.searchParams.get('search')).toBe('desk')
    expect(requests.at(-1)?.searchParams.get('category')).toBe('Furniture')
    expect(requests.at(-1)?.searchParams.get('page')).toBe('2')
    expect(screen.getByRole('searchbox', { name: 'Search products' })).toHaveValue('desk')
    expect(await screen.findByRole('combobox', { name: 'Category' })).toHaveValue('Furniture')
  })

  it.each(['abc', '0', '-4'])('starts at the first page for page=%s', async (page) => {
    const { requests } = setUp(shop, `/products?page=${page}`)

    await screen.findByRole('link', { name: 'Desk Lamp' })

    expect(requests.at(-1)?.searchParams.get('page')).toBe('1')
  })

  it('searches, puts the text in the address, and starts again at the first page', async () => {
    const user = userEvent.setup()
    const many = Array.from({ length: 30 }, (_, i) => aProduct(100 + i))
    const { router } = setUp([...many, lamp], '/products?page=2')
    await screen.findByText('Page 2 of 3')

    await user.type(screen.getByRole('searchbox', { name: 'Search products' }), 'lamp{Enter}')

    expect(await screen.findByRole('link', { name: 'Desk Lamp' })).toBeInTheDocument()
    expect(screen.getAllByRole('listitem')).toHaveLength(1)
    expect(router.state.location.search).toBe('?q=lamp')
  })

  it('filters by category and puts it in the address', async () => {
    const user = userEvent.setup()
    const { router } = setUp()
    await screen.findByRole('link', { name: 'Desk Lamp' })
    await waitFor(() => { expect(screen.getAllByRole('option')).toHaveLength(3) })

    await user.selectOptions(screen.getByRole('combobox', { name: 'Category' }), 'Electronics')

    await waitFor(() => { expect(screen.getAllByRole('listitem')).toHaveLength(1) })
    expect(screen.getByRole('link', { name: 'Wireless Headphones' })).toBeInTheDocument()
    expect(router.state.location.search).toBe('?category=Electronics')
  })

  it('moves between pages and keeps the filters', async () => {
    const user = userEvent.setup()
    const { router } = setUp(Array.from({ length: 30 }, (_, i) => aProduct(100 + i)))
    await screen.findByText('Page 1 of 3')
    const previous = screen.getByRole('button', { name: 'Previous' })
    expect(previous).toBeDisabled()

    await user.click(screen.getByRole('button', { name: 'Next' }))
    await screen.findByText('Page 2 of 3')
    expect(router.state.location.search).toBe('?page=2')

    await user.click(screen.getByRole('button', { name: 'Next' }))
    await screen.findByText('Page 3 of 3')
    expect(screen.getByRole('button', { name: 'Next' })).toBeDisabled()

    await user.click(screen.getByRole('button', { name: 'Previous' }))
    await screen.findByText('Page 2 of 3')
  })

  it('shows no pagination when everything fits on one page', async () => {
    setUp()
    await screen.findByRole('link', { name: 'Desk Lamp' })

    expect(screen.queryByRole('navigation', { name: 'Pagination' })).not.toBeInTheDocument()
  })

  it('says so when nothing matches, and clears the filters', async () => {
    const user = userEvent.setup()
    setUp(shop, '/products?q=zebra')
    expect(await screen.findByText('No products found.')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Clear filters' }))

    expect(await screen.findByRole('link', { name: 'Desk Lamp' })).toBeInTheDocument()
    expect(screen.getByRole('searchbox', { name: 'Search products' })).toHaveValue('')
  })

  it('says so when the page asked for is past the end', async () => {
    setUp(shop, '/products?page=99')

    expect(await screen.findByText('No products found.')).toBeInTheDocument()
  })

  it('says so when the products cannot be loaded, and tries again', async () => {
    const user = userEvent.setup()
    server.use(nobodyIsSignedIn(), http.get('/api/v1/products', () => HttpResponse.json({ message: 'Down.' }, { status: 503 })))
    renderApp('/products')
    expect(await screen.findByRole('alert')).toHaveTextContent('We could not load the products.')

    catalogHas(shop) // the service is back
    await user.click(screen.getByRole('button', { name: 'Try again' }))

    expect(await screen.findByRole('link', { name: 'Desk Lamp' })).toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  it('shows an https image and nothing for any other address', async () => {
    setUp([
      aProduct(1, { name: 'With picture', imageUrl: 'https://images.example.com/a.png' }),
      aProduct(2, { name: 'Script', imageUrl: 'javascript:alert(1)' }),
      aProduct(3, { name: 'Plain http', imageUrl: 'http://images.example.com/b.png' }),
    ])
    await screen.findByRole('link', { name: 'With picture' })

    // the pictures are decoration (the name is next to them), so their role is "presentation"
    const sources = screen.getAllByRole('presentation').map((image) => image.getAttribute('src'))
    expect(sources).toEqual(['https://images.example.com/a.png'])
  })
})
