import { z } from 'zod'
import { describe, expect, it } from 'vitest'
import './zodConfig'

describe('zodConfig', () => {
  it('switches off the parsers Zod compiles with new Function, which the Content-Security-Policy forbids', () => {
    expect(z.config().jitless).toBe(true)
  })

  it('still parses objects', () => {
    const schema = z.object({ id: z.guid(), quantity: z.number().int() })

    expect(schema.parse({ id: '00000000-0000-0000-0000-000000000001', quantity: 2, extra: true })).toEqual({
      id: '00000000-0000-0000-0000-000000000001',
      quantity: 2,
    })
    expect(schema.safeParse({ id: 'nope', quantity: 2 }).success).toBe(false)
  })
})
