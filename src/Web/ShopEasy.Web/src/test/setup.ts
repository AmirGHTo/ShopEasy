// Vitest has no global afterEach, so Testing Library cannot register its own clean-up
/* eslint-disable testing-library/no-manual-cleanup */
import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { afterEach, vi } from 'vitest'

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})
