import { http, HttpResponse } from 'msw'
import { describe, expect, it } from 'vitest'
import { UnexpectedResponseError } from '../../api/http'
import { aProduct, catalogHas, headphones } from '../../test/catalog'
import { server } from '../../test/server'
import { fetchCategories, fetchProducts } from './catalog'

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
