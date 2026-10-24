import { describe, expect, it } from 'vitest'
import { readOrdersQuery, writeOrdersQuery } from './ordersQuery'

describe('readOrdersQuery', () => {
  it('reads the status and the page', () => {
    expect(readOrdersQuery(new URLSearchParams('status=Confirmed&page=3'))).toEqual({ status: 'Confirmed', page: 3 })
  })

  it('has defaults for an empty address', () => {
    expect(readOrdersQuery(new URLSearchParams())).toEqual({ status: '', page: 1 })
  })

  it.each(['abc', '0', '-2', '1.5', '', 'Infinity'])('falls back to the first page for page=%s', (page) => {
    expect(readOrdersQuery(new URLSearchParams({ page })).page).toBe(1)
  })

  it.each(['Everything', 'confirmed', "Pending' OR 1=1", '<script>', 'Shipped'])('treats the status %j as no filter', (status) => {
    expect(readOrdersQuery(new URLSearchParams({ status })).status).toBe('')
  })
})

describe('writeOrdersQuery', () => {
  it('leaves out what is the default', () => {
    expect(writeOrdersQuery({ status: '', page: 1 }).toString()).toBe('')
  })

  it('writes what is set, and reads back the same', () => {
    const query = { status: 'PaymentFailed', page: 2 }

    expect(readOrdersQuery(writeOrdersQuery(query))).toEqual(query)
  })
})
