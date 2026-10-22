import { Link } from 'react-router'

export default function NotFoundPage({ message = 'There is nothing at this address.' }: { message?: string }) {
  return (
    <div className="flex flex-col items-start gap-3">
      <title>Page not found · ShopEasy</title>
      <h1 className="text-3xl font-semibold tracking-tight">Page not found</h1>
      <p className="text-slate-600">{message}</p>
      <Link to="/products" className="font-medium text-indigo-600 hover:text-indigo-500">
        Back to the products
      </Link>
    </div>
  )
}
