import { Link } from 'react-router'
import { formatPrice } from '../../lib/format'
import type { Product } from './catalog'
import ProductImage from './ProductImage'

export default function ProductCard({ product }: { product: Product }) {
  return (
    <li className="group relative flex flex-col gap-3 rounded-xl border border-slate-200 bg-white p-4 shadow-sm focus-within:ring-2 focus-within:ring-indigo-500">
      <ProductImage name={product.name} imageUrl={product.imageUrl} />
      <div className="flex flex-1 flex-col gap-1">
        <p className="text-xs font-medium uppercase tracking-wide text-slate-500">{product.category}</p>
        <h2 className="font-semibold text-slate-900">
          {/* the whole card is the click target: the link's ::after covers it */}
          <Link to={`/products/${product.id}`} className="outline-none after:absolute after:inset-0">
            {product.name}
          </Link>
        </h2>
        <p className="line-clamp-2 text-sm text-slate-600">{product.description}</p>
      </div>
      <p className="font-semibold text-slate-900">{formatPrice(product.price)}</p>
    </li>
  )
}
