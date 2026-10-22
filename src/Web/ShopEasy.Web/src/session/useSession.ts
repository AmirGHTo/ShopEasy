import { useQuery } from '@tanstack/react-query'
import { fetchSession } from './session'

export const sessionQueryKey = ['session'] as const

// `data` is the session, or null when nobody is signed in. Checked again when the tab regains
// focus, so a sign-out in another tab shows up here.
export function useSession() {
  return useQuery({
    queryKey: sessionQueryKey,
    queryFn: ({ signal }) => fetchSession(signal),
    staleTime: 60_000,
  })
}
