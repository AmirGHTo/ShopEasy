import { useSyncExternalStore } from 'react'
import { canAdd, itemCount } from './cart'
import { cartStore } from './cartStore'

export function useCart() {
  const lines = useSyncExternalStore(cartStore.subscribe, cartStore.getSnapshot)

  return {
    lines,
    count: itemCount(lines),
    canAdd: (productId: string) => canAdd(lines, productId),
    add: cartStore.add,
    setQuantity: cartStore.setQuantity,
    remove: cartStore.remove,
    clear: cartStore.clear,
  }
}
