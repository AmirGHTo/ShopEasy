// The one place that talks to the gateway. Same origin, so the browser sends the session cookie
// by itself and there is no token to handle here.

const REQUESTED_WITH = 'X-Requested-With'

// A request that changes something has to carry this header, or the gateway refuses it (a page of
// another site cannot add it). Reading needs nothing.
const CHANGES_STATE: ReadonlySet<string> = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])

export type Method = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

export interface RequestOptions {
  method?: Method
  body?: unknown
  headers?: Record<string, string>
  signal?: AbortSignal | undefined
}

// An answer from the gateway or a service that is not a success. The services explain themselves in
// JSON: `message`, a list of `errors` (order validation) or `error`, a short code (the gateway).
export class ApiError extends Error {
  readonly status: number
  readonly code: string | undefined
  readonly details: readonly string[]

  constructor(status: number, message: string, code?: string, details: readonly string[] = []) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
    this.details = details
  }
}

// Resolves with the JSON body (`undefined` for 204 No Content, ask for `undefined` then) and rejects
// with an ApiError for any other status. A dropped connection rejects with the fetch error itself.
export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, headers, signal } = options

  const sent: Record<string, string> = { Accept: 'application/json', ...headers }
  if (body !== undefined) sent['Content-Type'] = 'application/json'
  if (CHANGES_STATE.has(method)) sent[REQUESTED_WITH] = 'ShopEasy.Web'

  const init: RequestInit = { method, headers: sent }
  if (body !== undefined) init.body = JSON.stringify(body)
  if (signal) init.signal = signal

  const response = await fetch(path, init)

  if (!response.ok) throw await toApiError(response)
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

async function toApiError(response: Response): Promise<ApiError> {
  const problem = await readProblem(response)
  return new ApiError(
    response.status,
    problem.message ?? `The request failed (${response.status}).`,
    problem.error,
    problem.errors ?? [],
  )
}

interface Problem {
  message: string | undefined
  error: string | undefined
  errors: string[] | undefined
}

const UNKNOWN_PROBLEM: Problem = { message: undefined, error: undefined, errors: undefined }

async function readProblem(response: Response): Promise<Problem> {
  try {
    const body: unknown = await response.json()
    if (typeof body !== 'object' || body === null) return UNKNOWN_PROBLEM
    const { message, error, errors } = body as Record<string, unknown>
    return {
      message: typeof message === 'string' ? message : undefined,
      error: typeof error === 'string' ? error : undefined,
      errors: Array.isArray(errors) ? errors.filter((e): e is string => typeof e === 'string') : undefined,
    }
  } catch {
    return UNKNOWN_PROBLEM // an error page, an empty body: only the status is known
  }
}
