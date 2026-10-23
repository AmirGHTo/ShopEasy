import { Link, useParams } from 'react-router'
import { formatPrice } from '../../lib/format'
import NotFoundPage from '../../pages/NotFoundPage'
import ProductImage from './ProductImage'
import { useProduct } from './queries'

export default function ProductPage() {
  const { id = '' } = useParams()
  const product = useProduct(id)

  if (product.isPending) {
    return <div role="status" aria-label="Loading the product" className="h-96 animate-pulse rounded-xl bg-slate-200" />
  }

  if (product.isError) {
    return (
      <div role="alert" className="flex flex-col items-start gap-3 rounded-xl border border-red-200 bg-red-50 p-4">
        <p className="text-red-800">We could not load this product.</p>
        <button
          type="button"
          onClick={() => void product.refetch()}
          className="rounded-lg bg-white px-3 py-1.5 text-sm font-medium text-red-800 ring-1 ring-red-300 hover:bg-red-100"
        >
          Try again
        </button>
      </div>
    )
  }

  if (product.data === null) return <NotFoundPage message="There is no such product." />

  const { name, category, description, price, imageUrl, isActive } = product.data

  return (
    <article className="flex flex-col gap-6">
      <title>{`${name} · ShopEasy`}</title>
      <Link to="/products" className="text-sm font-medium text-indigo-600 hover:text-indigo-500">
        ← All products
      </Link>

      <div className="grid gap-8 md:grid-cols-2">
        <ProductImage name={name} imageUrl={imageUrl} />

        <div className="flex flex-col gap-4">
          <p className="text-xs font-medium uppercase tracking-wide text-slate-500">{category}</p>
          <h1 className="text-3xl font-semibold tracking-tight">{name}</h1>
          <p className="text-2xl font-semibold text-slate-900">{formatPrice(price)}</p>
          <p className="text-slate-700">{description}</p>

          {/* the catalog keeps a deleted product (orders refer to it) but it can no longer be bought */}
          {!isActive && (
            <p role="status" className="rounded-lg bg-amber-50 p-3 text-sm text-amber-900 ring-1 ring-amber-200">
              This product is no longer available.
            </p>
          )}
        </div>
      </div>
    </article>
  )
}
