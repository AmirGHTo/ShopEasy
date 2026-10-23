import type { UseQueryResult } from '@tanstack/react-query'
import { Link } from 'react-router'
import { formatCents, formatPrice, toCents } from '../../lib/format'
import type { Product } from '../catalog/catalog'
import type { CartLine } from './cart'
import QuantityField from './QuantityField'

interface CartRowProps {
  line: CartLine
  result: UseQueryResult<Product | null>
  onQuantity: (quantity: number) => void
  onRemove: () => void
}

const removeClass = 'text-sm font-medium text-slate-600 underline hover:text-slate-900'

export default function CartRow({ line, result, onQuantity, onRemove }: CartRowProps) {
  if (result.isPending) {
    return (
      <li aria-busy="true" className="p-4">
        <div className="h-6 w-1/2 animate-pulse rounded bg-slate-200" />
      </li>
    )
  }

  if (result.isError) {
    return (
      <li className="flex flex-wrap items-center justify-between gap-3 p-4">
        <p role="alert" className="text-red-800">We could not load a product in your cart.</p>
        <span className="flex gap-4">
          <button type="button" onClick={() => void result.refetch()} className={removeClass}>
            Try again
          </button>
          <button type="button" onClick={onRemove} className={removeClass}>
            Remove
          </button>
        </span>
      </li>
    )
  }

  const product = result.data

  // gone from the catalog, or no longer sold: it cannot be ordered, only taken out of the cart
  if (!product?.isActive) {
    return (
      <li className="flex flex-wrap items-center justify-between gap-3 p-4">
        <div>
          <p className="font-medium text-slate-700">{product ? product.name : 'A product that no longer exists'}</p>
          <p className="text-sm text-amber-800">No longer available</p>
        </div>
        <button type="button" onClick={onRemove} aria-label={`Remove ${product?.name ?? 'the unavailable product'}`} className={removeClass}>
          Remove
        </button>
      </li>
    )
  }

  return (
    <li className="flex flex-wrap items-center justify-between gap-4 p-4">
      <div className="min-w-0 flex-1">
        <h2 className="font-semibold text-slate-900">
          <Link to={`/products/${product.id}`} className="hover:text-indigo-600">
            {product.name}
          </Link>
        </h2>
        <p className="text-sm text-slate-600">{formatPrice(product.price)} each</p>
      </div>

      <QuantityField
        id={`quantity-${product.id}`}
        productName={product.name}
        quantity={line.quantity}
        onChange={onQuantity}
      />

      <p className="w-24 text-right font-semibold text-slate-900">{formatCents(toCents(product.price) * line.quantity)}</p>

      <button type="button" onClick={onRemove} aria-label={`Remove ${product.name}`} className={removeClass}>
        Remove
      </button>
    </li>
  )
}
