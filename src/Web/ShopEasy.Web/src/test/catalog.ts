import { http, HttpResponse } from 'msw'
import type { Product } from '../features/catalog/catalog'
import { server } from './server'

// The nth product of a made-up catalog; any field can be replaced
export function aProduct(n: number, overrides: Partial<Product> = {}): Product {
  return {
    id: `00000000-0000-0000-0000-${String(n).padStart(12, '0')}`,
    name: `Product ${String(n)}`,
    description: `About product ${String(n)}`,
    price: 10 + n,
    category: 'Electronics',
    imageUrl: '',
    isActive: true,
    ...overrides,
  }
}

export const headphones = aProduct(1, {
  name: 'Wireless Headphones',
  description: 'Premium noise-cancelling headphones',
  price: 299.99,
  category: 'Electronics',
})
export const desk = aProduct(2, { name: 'Standing Desk', description: 'Adjustable height', price: 599.99, category: 'Furniture' })
export const lamp = aProduct(3, { name: 'Desk Lamp', description: 'Warm light', price: 39.5, category: 'Furniture' })

// The Product API as far as the storefront uses it: it filters by search text (name or description)
// and category, pages, and lists the categories. `requests` shows what the app asked for.
export function catalogHas(products: readonly Product[]) {
  const requests: URL[] = []

  server.use(
    http.get('/api/v1/products/categories', () =>
      HttpResponse.json([...new Set(products.map((p) => p.category))].sort()),
    ),
    http.get('/api/v1/products/:id', ({ params }) => {
      const product = products.find((p) => p.id === params.id)
      return product ? HttpResponse.json(product) : new HttpResponse(null, { status: 404 })
    }),
    http.get('/api/v1/products', ({ request }) => {
      const url = new URL(request.url)
      requests.push(url)

      const search = (url.searchParams.get('search') ?? '').toLowerCase()
      const category = url.searchParams.get('category') ?? ''
      const page = Number(url.searchParams.get('page') ?? '1')
      const pageSize = Number(url.searchParams.get('pageSize') ?? '10')

      const matching = products.filter(
        (p) =>
          (category === '' || p.category === category) &&
          (search === '' || p.name.toLowerCase().includes(search) || p.description.toLowerCase().includes(search)),
      )
      return HttpResponse.json({
        items: matching.slice((page - 1) * pageSize, page * pageSize),
        totalCount: matching.length,
        page,
        pageSize,
      })
    }),
  )

  return requests
}
