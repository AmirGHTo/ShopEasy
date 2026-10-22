import { Outlet } from 'react-router'
import Header from './components/Header'

export default function Layout() {
  return (
    <>
      <a
        href="#content"
        className="sr-only focus:not-sr-only focus:absolute focus:left-4 focus:top-4 focus:rounded-lg focus:bg-white focus:px-3 focus:py-2"
      >
        Skip to content
      </a>
      <Header />
      <main id="content" className="mx-auto max-w-6xl p-6">
        <Outlet />
      </main>
    </>
  )
}
