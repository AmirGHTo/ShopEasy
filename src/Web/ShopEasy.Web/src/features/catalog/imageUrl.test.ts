import { describe, expect, it } from 'vitest'
import { safeImageSrc } from './imageUrl'

describe('safeImageSrc', () => {
  it('lets an https address through', () => {
    expect(safeImageSrc('https://images.example.com/chair.png')).toBe('https://images.example.com/chair.png')
  })

  it.each([
    '',
    'chair.png',
    '/images/chair.png',
    'http://images.example.com/chair.png',
    'javascript:alert(1)',
    'data:text/html;base64,PHNjcmlwdD4=',
    'file:///etc/passwd',
  ])('refuses %j', (url) => {
    expect(safeImageSrc(url)).toBeNull()
  })
})
