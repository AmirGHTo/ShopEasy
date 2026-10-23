import { describe, expect, it } from 'vitest'
import { readQuery, writeQuery } from './catalogQuery'

describe('readQuery', () => {
  it('reads the filters and the page', () => {
    const query = readQuery(new URLSearchParams('q=desk&category=Furniture&page=3'))

    expect(query).toEqual({ search: 'desk', category: 'Furniture', page: 3 })
  })

  it('has defaults for an empty address', () => {
    expect(readQuery(new URLSearchParams())).toEqual({ search: '', category: '', page: 1 })
  })

  it.each(['abc', '0', '-2', '1.5', '', 'Infinity', '1e999'])('falls back to the first page for page=%s', (page) => {
    expect(readQuery(new URLSearchParams({ page })).page).toBe(1)
  })

  it('trims the text and cuts it to the length the API accepts', () => {
    const query = readQuery(new URLSearchParams({ q: `  ${'x'.repeat(80)}  `, category: ' Books ' }))

    expect(query.search).toBe('x'.repeat(50))
    expect(query.category).toBe('Books')
  })
})

describe('writeQuery', () => {
  it('leaves out what is the default', () => {
    expect(writeQuery({ search: '', category: '', page: 1 }).toString()).toBe('')
  })

  it('writes what is set, and reads back the same', () => {
    const query = { search: 'desk lamp', category: 'Furniture', page: 2 }

    expect(readQuery(writeQuery(query))).toEqual(query)
  })
})
