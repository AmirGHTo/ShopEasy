import { Navigate, type RouteObject } from 'react-router'
import Layout from './Layout'
import NotFoundPage from './pages/NotFoundPage'
import RouteErrorPage from './pages/RouteErrorPage'

// Pages are loaded when first visited (code splitting): the first page load carries only the shell
export const routes: RouteObject[] = [
  {
    path: '/',
    element: <Layout />,
    errorElement: <RouteErrorPage />,
    children: [
      { index: true, element: <Navigate to="/products" replace /> },
      {
        path: 'products',
        lazy: async () => ({ Component: (await import('./features/catalog/ProductListPage')).default }),
      },
      {
        path: 'products/:id',
        lazy: async () => ({ Component: (await import('./features/catalog/ProductPage')).default }),
      },
      {
        path: 'cart',
        lazy: async () => ({ Component: (await import('./features/cart/CartPage')).default }),
      },
      {
        path: 'orders/:id',
        lazy: async () => ({ Component: (await import('./features/orders/OrderPage')).default }),
      },
      {
        path: 'checkout',
        lazy: async () => ({ Component: (await import('./features/checkout/CheckoutPage')).default }),
      },
      { path: '*', element: <NotFoundPage /> },
    ],
  },
]
