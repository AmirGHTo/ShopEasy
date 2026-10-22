import { Link, useLocation } from 'react-router'
import { useCart } from '../features/cart/useCart'
import { signInUrl, signOutUrl, type Session } from '../session/session'
import { useSession } from '../session/useSession'

export default function Header() {
  const { data: session, isPending, isError } = useSession()
  const { count } = useCart()

  return (
    <header className="border-b border-slate-200 bg-white">
      <div className="mx-auto flex max-w-5xl items-center justify-between px-6 py-4">
        <Link to="/" className="text-xl font-semibold tracking-tight">
          ShopEasy
        </Link>
        <nav aria-label="Account" aria-busy={isPending} className="flex items-center gap-4 text-sm">
          {isPending && <span aria-hidden="true" className="h-5 w-28 animate-pulse rounded bg-slate-200" />}
          {isError && <span className="text-slate-500">Your account is unavailable.</span>}
          {session === null && <SignInLink />}
          {session && <SignedIn session={session} />}
          <Link to="/cart" aria-label={`Cart, ${String(count)} ${count === 1 ? 'item' : 'items'}`} className={linkClass}>
            Cart
            <span className="ml-1 rounded-full bg-indigo-100 px-2 py-0.5 text-xs text-indigo-700">{count}</span>
          </Link>
        </nav>
      </div>
    </header>
  )
}

const linkClass = 'font-medium text-indigo-600 hover:text-indigo-500'

// Signing in and out are page loads of the gateway (it talks to Identity and comes back), so these
// are plain links and not router links
function SignInLink() {
  const { pathname, search } = useLocation()
  const here = pathname + search
  return (
    <a href={signInUrl(here)} className={linkClass}>
      Sign in
    </a>
  )
}

function SignedIn({ session }: { session: Session }) {
  return (
    <>
      <span className="text-slate-700">{session.name ?? session.email ?? 'Your account'}</span>
      <a href={signOutUrl} className={linkClass}>
        Sign out
      </a>
    </>
  )
}
