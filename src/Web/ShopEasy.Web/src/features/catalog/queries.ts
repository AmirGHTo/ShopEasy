import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { fetchCategories, fetchProducts, type CatalogQuery } from './catalog'

// One place for the cache keys, so that what is fetched and what is invalidated cannot drift apart
export const catalogKeys = {
  all: ['catalog'] as const,
  list: (query: CatalogQuery) => [...catalogKeys.all, 'list', query] as const,
  categories: () => [...catalogKeys.all, 'categories'] as const,
}

// While the next page loads, the previous one stays on screen (isPlaceholderData) instead of
// flashing to an empty list
export function useProducts(query: CatalogQuery) {
  return useQuery({
    queryKey: catalogKeys.list(query),
    queryFn: ({ signal }) => fetchProducts(query, signal),
    placeholderData: keepPreviousData,
  })
}

// Changes rarely, and the list works without it
export function useCategories() {
  return useQuery({
    queryKey: catalogKeys.categories(),
    queryFn: ({ signal }) => fetchCategories(signal),
    staleTime: 5 * 60_000,
  })
}
