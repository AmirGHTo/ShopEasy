import { MAX_SEARCH_LENGTH, type CatalogQuery } from './catalog'

export const FIRST_PAGE: CatalogQuery = { search: '', category: '', page: 1 }

// The address is user input: anything can be in it. Read it into a valid query, never throw.
export function readQuery(params: URLSearchParams): CatalogQuery {
  const page = Number(params.get('page'))
  return {
    search: (params.get('q') ?? '').trim().slice(0, MAX_SEARCH_LENGTH),
    category: (params.get('category') ?? '').trim(),
    page: Number.isInteger(page) && page >= 1 ? page : 1,
  }
}

// Only what differs from the defaults goes into the address, so the plain list has a plain address
export function writeQuery(query: CatalogQuery): URLSearchParams {
  const params = new URLSearchParams()
  if (query.search) params.set('q', query.search)
  if (query.category) params.set('category', query.category)
  if (query.page > 1) params.set('page', String(query.page))
  return params
}
