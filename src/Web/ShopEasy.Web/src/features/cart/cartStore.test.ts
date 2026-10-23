import { describe, expect, it, vi } from 'vitest'
import { MAX_QUANTITY } from './cart'
import { createCartStore, STORAGE_KEY } from './cartStore'

const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`

// A storage that remembers in a Map, like localStorage does for the length of a test
function fakeStorage(initial: Record<string, string> = {}) {
  const data = new Map(Object.entries(initial))
  return {
    data,
    getItem: (key: string) => data.get(key) ?? null,
    setItem: (key: string, value: string) => { data.set(key, value) },
  }
}

describe('cart store', () => {
  it('starts empty', () => {
    expect(createCartStore(() => fakeStorage()).getSnapshot()).toEqual([])
  })

  it('starts from what an earlier visit left in the browser', () => {
    const storage = fakeStorage({ [STORAGE_KEY]: JSON.stringify([{ productId: id(1), quantity: 2 }]) })

    expect(createCartStore(() => storage).getSnapshot()).toEqual([{ productId: id(1), quantity: 2 }])
  })

  it('starts empty from storage that was damaged', () => {
    const storage = fakeStorage({ [STORAGE_KEY]: '{"broken' })

    expect(createCartStore(() => storage).getSnapshot()).toEqual([])
  })

  it('writes every change to the browser, for the next visit', () => {
    const storage = fakeStorage()
    const store = createCartStore(() => storage)

    store.add(id(1))
    store.add(id(1))
    store.setQuantity(id(1), 5)
    store.add(id(2))
    store.remove(id(2))

    expect(JSON.parse(storage.data.get(STORAGE_KEY) ?? 'null')).toEqual([{ productId: id(1), quantity: 5 }])
    expect(createCartStore(() => storage).getSnapshot()).toEqual([{ productId: id(1), quantity: 5 }])
  })

  it('empties the cart', () => {
    const storage = fakeStorage()
    const store = createCartStore(() => storage)
    store.add(id(1))

    store.clear()

    expect(store.getSnapshot()).toEqual([])
    expect(storage.data.get(STORAGE_KEY)).toBe('[]')
  })

  it('tells the listeners about a change, and only about a change', () => {
    const store = createCartStore(() => fakeStorage())
    const listener = vi.fn()
    store.subscribe(listener)

    store.add(id(1))
    expect(listener).toHaveBeenCalledTimes(1)

    store.remove(id(2)) // not in the cart
    store.setQuantity(id(1), 1) // already 1
    expect(listener).toHaveBeenCalledTimes(1)
  })

  it('keeps the same snapshot object until the cart changes', () => {
    const store = createCartStore(() => fakeStorage())
    store.add(id(1))
    const before = store.getSnapshot()

    store.remove(id(2))
    expect(store.getSnapshot()).toBe(before)

    store.add(id(1))
    expect(store.getSnapshot()).not.toBe(before)
  })

  it('stops telling a listener that has left', () => {
    const store = createCartStore(() => fakeStorage())
    const listener = vi.fn()
    const leave = store.subscribe(listener)

    leave()
    store.add(id(1))

    expect(listener).not.toHaveBeenCalled()
  })

  it('keeps the limit of one product', () => {
    const store = createCartStore(() => fakeStorage())

    store.add(id(1))
    store.setQuantity(id(1), 10_000)

    expect(store.getSnapshot()).toEqual([{ productId: id(1), quantity: MAX_QUANTITY }])
  })

  it('works for this visit when the browser will not store anything', () => {
    const refusing = {
      getItem: () => { throw new Error('storage is blocked') },
      setItem: () => { throw new Error('quota exceeded') },
    }
    const store = createCartStore(() => refusing)
    const listener = vi.fn()
    store.subscribe(listener)

    store.add(id(1))

    expect(store.getSnapshot()).toEqual([{ productId: id(1), quantity: 1 }])
    expect(listener).toHaveBeenCalled()
  })

  it('works when there is no storage at all', () => {
    const store = createCartStore(() => undefined)

    store.add(id(1))

    expect(store.getSnapshot()).toHaveLength(1)
  })

  it('follows a change made in another tab, and ignores other keys', () => {
    const storage = fakeStorage()
    const store = createCartStore(() => storage)
    const listener = vi.fn()
    const leave = store.subscribe(listener)

    storage.data.set(STORAGE_KEY, JSON.stringify([{ productId: id(7), quantity: 3 }]))
    window.dispatchEvent(new StorageEvent('storage', { key: STORAGE_KEY }))
    expect(store.getSnapshot()).toEqual([{ productId: id(7), quantity: 3 }])
    expect(listener).toHaveBeenCalledTimes(1)

    window.dispatchEvent(new StorageEvent('storage', { key: 'something.else' }))
    expect(listener).toHaveBeenCalledTimes(1)

    leave()
    window.dispatchEvent(new StorageEvent('storage', { key: STORAGE_KEY }))
    expect(listener).toHaveBeenCalledTimes(1)
  })
})
