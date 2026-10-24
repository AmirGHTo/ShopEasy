import { signInUrl } from '../session/session'

// For a page that is only for a signed-in customer. Signing in is a page load of the gateway, which
// brings the customer back to `returnTo`.
export default function SignInPrompt({ message, returnTo }: { message: string; returnTo: string }) {
  return (
    <div className="flex flex-col items-start gap-3 rounded-xl border border-slate-200 bg-white p-6">
      <p className="text-slate-700">{message}</p>
      <a href={signInUrl(returnTo)} className="font-medium text-indigo-600 hover:text-indigo-500">
        Sign in
      </a>
    </div>
  )
}
