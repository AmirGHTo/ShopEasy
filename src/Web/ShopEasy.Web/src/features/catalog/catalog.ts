import { z } from 'zod'
import { request } from '../../api/http'

// Twelve fill a grid of 2, 3 or 4 columns without a gap in the last row
export const PAGE_SIZE = 12

// The Product API refuses a longer search text (400); stop at the input so the user never sees that
export const MAX_SEARCH_LENGTH = 50

export const productSchema = z.object({
  id: z.uuid(),
  name: z.string(),
  description: z.string(),
  price: z.number(),
  category: z.string(),
  imageUrl: z.string(),
  isActive: z.boolean(),
})

export type Product = z.infer<typeof productSchema>

const productPageSchema = z.object({
  items: z.array(productSchema),
  totalCount: z.number().int().nonnegative(),
  page: z.number().int().positive(),
  pageSize: z.number().int().positive(),
})

export type ProductPage = z.infer<typeof productPageSchema>

// What the list shows. Kept in the address (?q=&category=&page=), so a search can be bookmarked,
// shared, reloaded and walked back with the browser's back button.
export interface CatalogQuery {
  search: string
  category: string
  page: number
}

export function fetchProducts(query: CatalogQuery, signal?: AbortSignal): Promise<ProductPage> {
  const params = new URLSearchParams({ page: String(query.page), pageSize: String(PAGE_SIZE) })
  if (query.search) params.set('search', query.search)
  if (query.category) params.set('category', query.category)

  return request(`/api/v1/products?${params.toString()}`, productPageSchema, { signal })
}

export function fetchCategories(signal?: AbortSignal): Promise<string[]> {
  return request('/api/v1/products/categories', z.array(z.string()), { signal })
}
