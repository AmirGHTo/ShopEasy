import { isRouteErrorResponse, Link, useRouteError } from 'react-router'
import NotFoundPage from './NotFoundPage'

// What a page shows when it breaks while rendering (or a route answers with an error): the shell
// stays, and the details go to the console, not to the visitor
export default function RouteErrorPage() {
  const error = useRouteError()
  if (isRouteErrorResponse(error) && error.status === 404) return <NotFoundPage />

  console.error(error)
  return (
    <div role="alert" className="flex flex-col items-start gap-3">
      <h1 className="text-3xl font-semibold tracking-tight">Something went wrong</h1>
      <p className="text-slate-600">Please try again in a moment.</p>
      <Link to="/products" className="font-medium text-indigo-600 hover:text-indigo-500">
        Back to the products
      </Link>
    </div>
  )
}
