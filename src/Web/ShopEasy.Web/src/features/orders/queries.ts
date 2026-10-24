import { useQuery, useQueryClient } from '@tanstack/react-query'
import { fetchOrder, isSettled } from './orders'

export const orderKeys = {
  all: ['orders'] as const,
  detail: (id: string) => [...orderKeys.all, 'detail', id] as const,
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
