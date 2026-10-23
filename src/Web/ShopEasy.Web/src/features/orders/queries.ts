import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { cancelOrder } from './cancel'
import { fetchOrder, fetchOrders, isSettled, type OrdersQuery } from './orders'

export const orderKeys = {
  all: ['orders'] as const,
  detail: (id: string) => [...orderKeys.all, 'detail', id] as const,
  list: (query: OrdersQuery) => [...orderKeys.all, 'list', query] as const,
  // the look-outs after a cancel was asked for, apart from the order's own query: they have their own
  // count of looks
  cancelling: (id: string) => [...orderKeys.all, 'cancelling', id] as const,
}

// The old page stays on screen while the next one loads
export function useOrders(query: OrdersQuery) {
  return useQuery({
    queryKey: orderKeys.list(query),
    queryFn: ({ signal }) => fetchOrders(query, signal),
    placeholderData: keepPreviousData,
  })
}

// How often a Pending order is looked at again, and how many times: 30 looks two seconds apart is a
// minute, long enough for the saga (stock, payment) and short enough not to ask forever
export const POLL_MS = 2000
export const MAX_POLLS = 30

// data is the order, or null when there is no such order of this customer. An order that is still
// Pending is asked about again until it settles (or the looks run out); a settled one, a missing
// one and a failure are not. Polling pauses while the tab is hidden. `looks` is how many times the
// order has been read so far.
export function useOrder(id: string, polling: { pollMs: number; maxPolls: number }) {
  const client = useQueryClient()
  const query = useQuery({
    queryKey: orderKeys.detail(id),
    queryFn: ({ signal }) => fetchOrder(id, signal),
    refetchInterval: (query) => {
      const order = query.state.data
      if (!order || isSettled(order.statusName)) return false
      return query.state.dataUpdateCount < polling.maxPolls ? polling.pollMs : false
    },
  })

  return { ...query, looks: client.getQueryState(orderKeys.detail(id))?.dataUpdateCount ?? 0 }
}

// Cancelling an order. The Order API may answer "done" or "cancelling": stock is released and a payment
// refunded first, and the order is Cancelled a moment later. So once the request is accepted, the order
// is looked at again until it says Cancelled (or the looks run out), and the page's own copy of the
// order is replaced with it.
export function useCancelOrder(id: string, polling: { pollMs: number; maxPolls: number }) {
  const client = useQueryClient()
  const cancel = useMutation({ mutationFn: () => cancelOrder(id) })

  const lookouts = useQuery({
    queryKey: orderKeys.cancelling(id),
    enabled: cancel.isSuccess,
    queryFn: async ({ signal }) => {
      const order = await fetchOrder(id, signal)
      if (order?.statusName === 'Cancelled') {
        client.setQueryData(orderKeys.detail(id), order)
        // the lists of orders show the old status until they are asked again
        void client.invalidateQueries({ queryKey: [...orderKeys.all, 'list'] })
      }
      return order
    },
    refetchInterval: (query) => {
      const order = query.state.data
      if (order?.statusName === 'Cancelled') return false
      return query.state.dataUpdateCount < polling.maxPolls ? polling.pollMs : false
    },
  })

  // dataUpdatedAt is read so that the page is drawn again after every look, also one that found the
  // same Confirmed as before (the data is then the very same object, which alone would not redraw it)
  const { data, dataUpdatedAt, refetch } = lookouts
  const looks = dataUpdatedAt > 0 ? (client.getQueryState(orderKeys.cancelling(id))?.dataUpdateCount ?? 0) : 0
  const done = data?.statusName === 'Cancelled'
  return {
    cancel,
    // asked for, accepted, and not yet Cancelled
    cancelling: cancel.isSuccess && !done,
    // the looks ran out before it said Cancelled
    gaveUp: cancel.isSuccess && !done && looks >= polling.maxPolls,
    refresh: () => void refetch(),
  }
}
