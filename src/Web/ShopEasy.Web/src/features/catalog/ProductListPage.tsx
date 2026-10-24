import { useSearchParams } from 'react-router'
import Pagination from '../../components/Pagination'
import { PAGE_SIZE, type CatalogQuery } from './catalog'
import CatalogFilterBar from './CatalogFilterBar'
import { readQuery, writeQuery } from './catalogQuery'
import ProductCard from './ProductCard'
import { useCategories, useProducts } from './queries'

export default function ProductListPage() {
  const [params, setParams] = useSearchParams()
  const query = readQuery(params)

  // A change of filter starts again at the first page; a change of page keeps the filters
  const change = (changes: Partial<CatalogQuery>) => {
    const isPageChange = 'page' in changes
    setParams(writeQuery({ ...query, ...(isPageChange ? {} : { page: 1 }), ...changes }))
  }

  const products = useProducts(query)
  const categories = useCategories()

  return (
    <div className="flex flex-col gap-6">
      <title>Products · ShopEasy</title>
      <h1 className="text-3xl font-semibold tracking-tight">Products</h1>

      <CatalogFilterBar query={query} categories={categories.data ?? []} onChange={change} />

      {products.isPending && <ProductGridPlaceholder />}

      {products.isError && (
        <div role="alert" className="flex flex-col items-start gap-3 rounded-xl border border-red-200 bg-red-50 p-4">
          <p className="text-red-800">We could not load the products.</p>
          <button
            type="button"
            onClick={() => void products.refetch()}
            className="rounded-lg bg-white px-3 py-1.5 text-sm font-medium text-red-800 ring-1 ring-red-300 hover:bg-red-100"
          >
            Try again
          </button>
        </div>
      )}

      {products.data &&
        (products.data.items.length === 0 ? (
          <div className="flex flex-col items-start gap-3 rounded-xl border border-slate-200 bg-white p-6">
            <p className="text-slate-700">No products found.</p>
            <button
              type="button"
              onClick={() => { setParams(writeQuery({ search: '', category: '', page: 1 })) }}
              className="text-sm font-medium text-indigo-600 hover:text-indigo-500"
            >
              Clear filters
            </button>
          </div>
        ) : (
          <>
            <ul
              aria-busy={products.isPlaceholderData}
              className={`grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 ${products.isPlaceholderData ? 'opacity-60' : ''}`}
            >
              {products.data.items.map((product) => (
                <ProductCard key={product.id} product={product} />
              ))}
            </ul>
            <Pagination
              page={products.data.page}
              totalPages={Math.ceil(products.data.totalCount / PAGE_SIZE)}
              onPageChange={(page) => { change({ page }) }}
            />
          </>
        ))}
    </div>
  )
}

function ProductGridPlaceholder() {
  return (
    <div role="status" aria-label="Loading products" className="grid grid-cols-1 gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
      {Array.from({ length: 8 }, (_, index) => (
        <div key={index} className="h-72 animate-pulse rounded-xl bg-slate-200" />
      ))}
    </div>
  )
}
