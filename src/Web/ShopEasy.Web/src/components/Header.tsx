import { signInUrl, signOutUrl, type Session } from '../session/session'
import { useSession } from '../session/useSession'

export default function Header() {
  const { data: session, isPending, isError } = useSession()

  return (
    <header className="border-b border-slate-200 bg-white">
      <div className="mx-auto flex max-w-5xl items-center justify-between px-6 py-4">
        <a href="/" className="text-xl font-semibold tracking-tight">
          ShopEasy
        </a>
        <nav aria-label="Account" aria-busy={isPending} className="flex items-center gap-4 text-sm">
          {isPending && <span aria-hidden="true" className="h-5 w-28 animate-pulse rounded bg-slate-200" />}
          {isError && <span className="text-slate-500">Your account is unavailable.</span>}
          {session === null && <SignInLink />}
          {session && <SignedIn session={session} />}
        </nav>
      </div>
    </header>
  )
}

const linkClass = 'font-medium text-indigo-600 hover:text-indigo-500'

function SignInLink() {
  const here = window.location.pathname + window.location.search
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
