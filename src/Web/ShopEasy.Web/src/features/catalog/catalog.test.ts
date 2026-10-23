import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { UnexpectedResponseError } from '../../api/http'
import { aProduct, catalogHas, headphones } from '../../test/catalog'
import { server } from '../../test/server'
import { fetchCategories, fetchProduct, fetchProducts } from './catalog'

describe('fetchProducts', () => {
  it('asks for the page, the page size, the search text and the category', async () => {
    const requests = catalogHas([headphones])

    await fetchProducts({ search: 'head phones', category: 'Electronics', page: 2 })

    expect(requests).toHaveLength(1)
    expect(Object.fromEntries(requests[0]?.searchParams ?? [])).toEqual({
      page: '2',
      pageSize: '12',
      search: 'head phones',
      category: 'Electronics',
    })
  })

  it('leaves out an empty search and an empty category', async () => {
    const requests = catalogHas([headphones])

    await fetchProducts({ search: '', category: '', page: 1 })

    expect(Object.fromEntries(requests[0]?.searchParams ?? [])).toEqual({ page: '1', pageSize: '12' })
  })

  it('returns the page the API sent', async () => {
    catalogHas([headphones, aProduct(5)])

    const page = await fetchProducts({ search: '', category: '', page: 1 })

    expect(page.totalCount).toBe(2)
    expect(page.items.map((p) => p.name)).toEqual(['Wireless Headphones', 'Product 5'])
  })

  // The seeded catalog's ids are 00000000-0000-0000-0000-00000000000N: Guids, but not RFC UUIDs. A
  // check for "a real UUID" made the whole list fail to load in the real stack.
  it('accepts the ids of the seeded catalog', async () => {
    const seeded = aProduct(4, { id: '00000000-0000-0000-0000-000000000004', name: 'Ergonomic Office Chair' })
    catalogHas([seeded])

    const page = await fetchProducts({ search: '', category: '', page: 1 })

    expect(page.items.map((p) => p.id)).toEqual(['00000000-0000-0000-0000-000000000004'])
  })

  it('refuses a product whose id is not a UUID', async () => {
    server.use(
      http.get('/api/v1/products', () =>
        HttpResponse.json({ items: [{ ...headphones, id: '../../admin' }], totalCount: 1, page: 1, pageSize: 12 }),
      ),
    )

    await expect(fetchProducts({ search: '', category: '', page: 1 })).rejects.toBeInstanceOf(UnexpectedResponseError)
  })
})

describe('fetchCategories', () => {
  it('returns the category names', async () => {
    catalogHas([headphones, aProduct(2, { category: 'Books' })])

    await expect(fetchCategories()).resolves.toEqual(['Books', 'Electronics'])
  })
})

describe('fetchProduct', () => {
  it('finds a product with a seeded id', async () => {
    catalogHas([aProduct(4, { id: '00000000-0000-0000-0000-000000000004', name: 'Ergonomic Office Chair' })])

    await expect(fetchProduct('00000000-0000-0000-0000-000000000004')).resolves.toMatchObject({
      name: 'Ergonomic Office Chair',
    })
  })

  it('does not ask the API for something that is not an id', async () => {
    catalogHas([headphones])

    await expect(fetchProduct('../../admin')).resolves.toBeNull()
  })
})
