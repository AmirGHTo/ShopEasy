import type { CartLine } from '../cart/cart'

const STORAGE_KEY = 'shopeasy.checkout.v1'

// One checkout attempt is one idempotency key. Placing the same order again with the same key is
// safe (the server returns the order it already placed); a different order needs a different key,
// or the server refuses it. So the key belongs to what is being bought, and lives exactly as long
// as that does: while the cart and the note stay the same, every try (a retry after a dropped
// connection, a click after a reload) uses the same key. It is kept in sessionStorage, which a
// reload keeps and a new tab does not, because a reload in the middle of placing an order is the
// case it exists for.
//
// What identifies the order is the same as what the server compares: the products and quantities
// (in any order) and the note.
export function fingerprintOf(lines: readonly CartLine[], notes: string): string {
  const sorted = [...lines].sort((a, b) => a.productId.localeCompare(b.productId))
  return JSON.stringify({ lines: sorted.map(({ productId, quantity }) => [productId, quantity]), notes: notes.trim() })
}

interface StoredAttempt {
  fingerprint: string
  key: string
}

function read(): StoredAttempt | null {
  try {
    const parsed: unknown = JSON.parse(sessionStorage.getItem(STORAGE_KEY) ?? 'null')
    if (typeof parsed !== 'object' || parsed === null) return null
    const { fingerprint, key } = parsed as Record<string, unknown>
    return typeof fingerprint === 'string' && typeof key === 'string' && KEY_FORMAT.test(key) ? { fingerprint, key } : null
  } catch {
    return null // not there, damaged, or storage is blocked
  }
}

// The Order API accepts 1-128 characters of letters, digits and . _ ~ : -
const KEY_FORMAT = /^[A-Za-z0-9._~:-]{1,128}$/

// A random version-4 UUID. crypto.randomUUID() only exists on pages served over HTTPS or from
// localhost; getRandomValues works everywhere.
export function newIdempotencyKey(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16))
  bytes[6] = ((bytes[6] ?? 0) & 0x0f) | 0x40
  bytes[8] = ((bytes[8] ?? 0) & 0x3f) | 0x80
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('')
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`
}

// The key for this order: the one already used for it, or a new one
export function keyForAttempt(fingerprint: string): string {
  const stored = read()
  if (stored?.fingerprint === fingerprint) return stored.key

  const key = newIdempotencyKey()
  try {
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify({ fingerprint, key } satisfies StoredAttempt))
  } catch {
    // no storage: the key lives for this page only, which still protects against a double click
  }
  return key
}

// The order was placed (or given up on): the next one starts with a new key
export function forgetAttempt(): void {
  try {
    sessionStorage.removeItem(STORAGE_KEY)
  } catch {
    // nothing to forget
  }
}
