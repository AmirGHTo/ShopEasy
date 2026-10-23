import { vi } from 'vitest'

// Replaces fetch for one test; the answers are handed out in order. Returns the mock, so a test can
// look at what was sent.
export function stubFetch(...answers: Response[]) {
  const mock = vi.fn<typeof fetch>()
  for (const answer of answers) mock.mockResolvedValueOnce(answer)
  vi.stubGlobal('fetch', mock)
  return mock
}

export function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

// What the nth call to fetch was given
export function sentRequest(mock: ReturnType<typeof stubFetch>, call = 0) {
  const sent = mock.mock.calls[call]
  if (!sent) throw new Error(`fetch was not called ${String(call + 1)} time(s)`)
  const [path, init] = sent
  return { path, init, headers: (init?.headers ?? {}) as Record<string, string> }
}
