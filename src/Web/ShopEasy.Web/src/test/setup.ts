// Vitest has no global afterEach, so Testing Library cannot register its own clean-up
/* eslint-disable testing-library/no-manual-cleanup */
import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterAll, afterEach, beforeAll } from 'vitest'
import { cartStore } from '../features/cart/cartStore'
import { server } from './server'

// A call nobody described fails the test, so a forgotten handler is never mistaken for an answer
beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' })
})

afterEach(() => {
  cleanup()
  server.resetHandlers()
  cartStore.clear() // each test starts with an empty cart
})

afterAll(() => {
  server.close()
})
