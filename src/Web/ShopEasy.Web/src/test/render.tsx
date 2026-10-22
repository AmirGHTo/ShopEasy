import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import type { ReactElement } from 'react'
import { createMemoryRouter, type RouteObject } from 'react-router'
import { RouterProvider } from 'react-router/dom'
import { routes } from '../routes'

// A fresh cache per test, and no retries, so that a failing request shows up at once
function freshClient() {
  return new QueryClient({ defaultOptions: { queries: { retry: false } } })
}

function renderRoutes(routeObjects: RouteObject[], address: string) {
  const router = createMemoryRouter(routeObjects, { initialEntries: [address] })
  const view = render(
    <QueryClientProvider client={freshClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return { ...view, router }
}

// One component on its own, at an address (it can read the address and use links)
export function renderWithProviders(ui: ReactElement, address = '/') {
  return renderRoutes([{ path: '*', element: ui }], address)
}

// The whole app, as the browser would open it at this address
export function renderApp(address: string) {
  return renderRoutes(routes, address)
}
