import { Link, useSearchParams } from 'react-router'
import { ApiError } from '../../api/http'
import Pagination from '../../components/Pagination'
import SignInPrompt from '../../components/SignInPrompt'
import { formatDateTime } from '../../lib/format'
import { ORDER_STATUS_FILTERS, ORDERS_PAGE_SIZE, type OrdersQuery } from './orders'
import { readOrdersQuery, writeOrdersQuery } from './ordersQuery'
import { useOrders } from './queries'
import StatusBadge from './StatusBadge'

const linkClass = 'font-medium text-indigo-600 hover:text-indigo-500'

// The customer's orders, newest first. Which page and which status are in the address, so they can
// be bookmarked and walked back with the back button.
export default function OrdersPage() {
  const [params, setParams] = useSearchParams()
  const query = readOrdersQuery(params)
  const orders = useOrders(query)

  // a change of filter starts at the first page; a change of page keeps the filter
  const change = (changes: Partial<OrdersQuery>) => {
    setParams(writeOrdersQuery({ ...query, ...('page' in changes ? {} : { page: 1 }), ...changes }))
  }

  return (
    <div className="flex flex-col gap-6">
      <title>Your orders · ShopEasy</title>
      <h1 className="text-3xl font-semibold tracking-tight">Your orders</h1>

      {orders.isError && orders.error instanceof ApiError && orders.error.status === 401 && (
        <SignInPrompt message="Sign in to see your orders." returnTo="/orders" />
      )}

      {orders.isError && !(orders.error instanceof ApiError && orders.error.status === 401) && (
        <div role="alert" className="flex flex-col items-start gap-3 rounded-xl border border-red-200 bg-red-50 p-4">
          <p className="text-red-800">We could not load your orders.</p>
          <button
            type="button"
            onClick={() => void orders.refetch()}
            className="rounded-lg bg-white px-3 py-1.5 text-sm font-medium text-red-800 ring-1 ring-red-300 hover:bg-red-100"
          >
            Try again
          </button>
        </div>
      )}

      {!orders.isError && (
        <div className="flex flex-col gap-1 self-start">
          <label htmlFor="status" className="text-sm font-medium text-slate-700">
            Show
          </label>
          <select
            id="status"
            value={query.status}
            onChange={(event) => { change({ status: event.target.value }) }}
            className="rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500"
          >
            {ORDER_STATUS_FILTERS.map((filter) => (
              <option key={filter.value} value={filter.value}>
                {filter.label}
              </option>
            ))}
          </select>
        </div>
      )}

      {orders.isPending && <div role="status" aria-label="Loading your orders" className="h-48 animate-pulse rounded-xl bg-slate-200" />}

      {orders.data &&
        (orders.data.items.length === 0 ? (
          <div className="flex flex-col items-start gap-3 rounded-xl border border-slate-200 bg-white p-6">
            <p className="text-slate-700">
              {query.status === '' ? 'You have not placed any orders yet.' : 'No orders with this status.'}
            </p>
            <Link to="/products" className={linkClass}>
              Browse the products
            </Link>
          </div>
        ) : (
          <>
            <ul
              aria-busy={orders.isPlaceholderData}
              className={`flex flex-col divide-y divide-slate-200 rounded-xl border border-slate-200 bg-white ${orders.isPlaceholderData ? 'opacity-60' : ''}`}
            >
              {orders.data.items.map((order) => (
                <li key={order.orderId} className="flex flex-wrap items-center justify-between gap-3 p-4">
                  <div className="flex min-w-0 flex-col gap-1">
                    <Link to={`/orders/${order.orderId}`} className={linkClass}>
                      Order of {formatDateTime(order.placedAt)}
                    </Link>
                    <span className="truncate text-sm text-slate-600">{order.itemsSummary}</span>
                  </div>
                  <div className="flex items-center gap-4">
                    <StatusBadge statusName={order.statusName} />
                    <span className="font-semibold text-slate-900">{order.amountDisplay}</span>
                  </div>
                </li>
              ))}
            </ul>
            <Pagination
              page={orders.data.page}
              totalPages={Math.ceil(orders.data.total / ORDERS_PAGE_SIZE)}
              onPageChange={(page) => { change({ page }) }}
            />
          </>
        ))}
    </div>
  )
}
