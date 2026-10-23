// An image address comes from the catalog, which an administrator edits: only https is shown, so
// nothing else (a script URL, a local file) can ever end up in the page.
export function safeImageSrc(url: string): string | null {
  try {
    return new URL(url).protocol === 'https:' ? url : null
  } catch {
    return null
  }
}
