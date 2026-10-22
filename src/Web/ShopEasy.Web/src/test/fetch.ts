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

// The headers of the nth call as a plain object
export function sentHeaders(mock: ReturnType<typeof stubFetch>, call = 0): Record<string, string> {
  return mock.mock.calls[call][1]?.headers as Record<string, string>
}
