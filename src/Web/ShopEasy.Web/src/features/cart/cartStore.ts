import { addToCart, EMPTY_CART, parseStoredCart, removeFromCart, setQuantity, type Cart } from './cart'

export const STORAGE_KEY = 'shopeasy.cart.v1'

type CartStorage = Pick<Storage, 'getItem' | 'setItem'>

// Reading window.localStorage can itself throw (storage blocked by the browser's settings)
function browserStorage(): CartStorage | undefined {
  try {
    return window.localStorage
  } catch {
    return undefined
  }
}

// The cart lives in the browser (localStorage), outside React, so that it survives a reload and a
// closed tab, and is the same in every tab. React reads it with useSyncExternalStore (see useCart):
// the hook's two needs are subscribe() and getSnapshot(), and the snapshot must stay the same
// object until the cart really changes.
export function createCartStore(storage: () => CartStorage | undefined = browserStorage) {
  const listeners = new Set<() => void>()
  let cart = load()

  function load(): Cart {
    try {
      return parseStoredCart(storage()?.getItem(STORAGE_KEY) ?? null)
    } catch {
      return EMPTY_CART
    }
  }

  function commit(next: Cart) {
    if (next === cart) return
    cart = next
    try {
      storage()?.setItem(STORAGE_KEY, JSON.stringify(next))
    } catch {
      // storage is full or forbidden (private mode): the cart still works for this visit
    }
    listeners.forEach((listener) => { listener() })
  }

  // Another tab changed the cart: the browser tells this one (it never fires in the tab that wrote)
  function onStorageEvent(event: StorageEvent) {
    if (event.key !== null && event.key !== STORAGE_KEY) return
    cart = load()
    listeners.forEach((listener) => { listener() })
  }

  return {
    getSnapshot: (): Cart => cart,

    subscribe: (listener: () => void): (() => void) => {
      if (listeners.size === 0) window.addEventListener('storage', onStorageEvent)
      listeners.add(listener)
      return () => {
        listeners.delete(listener)
        if (listeners.size === 0) window.removeEventListener('storage', onStorageEvent)
      }
    },

    add: (productId: string) => { commit(addToCart(cart, productId)) },
    setQuantity: (productId: string, quantity: number) => { commit(setQuantity(cart, productId, quantity)) },
    remove: (productId: string) => { commit(removeFromCart(cart, productId)) },
    clear: () => { commit(EMPTY_CART) },
  }
}

export type CartStore = ReturnType<typeof createCartStore>

// The one cart of this browser
export const cartStore = createCartStore()
