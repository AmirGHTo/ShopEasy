import { ORDER_STATUS_FILTERS, type OrdersQuery } from './orders'

// The address is user input: anything can be in it. Read it into a valid query, never throw. A
// status that is not one of the filters is no filter.
export function readOrdersQuery(params: URLSearchParams): OrdersQuery {
  const page = Number(params.get('page'))
  const status = params.get('status') ?? ''
  return {
    status: ORDER_STATUS_FILTERS.some((filter) => filter.value === status) ? status : '',
    page: Number.isInteger(page) && page >= 1 ? page : 1,
  }
}

// Only what differs from the defaults goes into the address
export function writeOrdersQuery(query: OrdersQuery): URLSearchParams {
  const params = new URLSearchParams()
  if (query.status) params.set('status', query.status)
  if (query.page > 1) params.set('page', String(query.page))
  return params
}
