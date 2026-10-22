import type { SyntheticEvent } from 'react'
import { MAX_SEARCH_LENGTH, type CatalogQuery } from './catalog'

interface CatalogFilterBarProps {
  query: CatalogQuery
  categories: readonly string[]
  onChange: (changes: Partial<CatalogQuery>) => void
}

const fieldClass =
  'rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm text-slate-900 focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500'

export default function CatalogFilterBar({ query, categories, onChange }: CatalogFilterBarProps) {
  const submitSearch = (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault()
    const text = new FormData(event.currentTarget).get('search')
    onChange({ search: typeof text === 'string' ? text.trim() : '' })
  }

  return (
    <div className="flex flex-col gap-3 sm:flex-row sm:items-end">
      <form role="search" onSubmit={submitSearch} className="flex flex-1 gap-2">
        <div className="flex flex-1 flex-col gap-1">
          <label htmlFor="search" className="text-sm font-medium text-slate-700">
            Search products
          </label>
          {/* keyed by the address: "Clear filters" and the back button put the text back too */}
          <input
            key={query.search}
            id="search"
            name="search"
            type="search"
            defaultValue={query.search}
            maxLength={MAX_SEARCH_LENGTH}
            className={fieldClass}
          />
        </div>
        <button
          type="submit"
          className="self-end rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white hover:bg-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2"
        >
          Search
        </button>
      </form>

      <div className="flex flex-col gap-1">
        <label htmlFor="category" className="text-sm font-medium text-slate-700">
          Category
        </label>
        <select
          id="category"
          value={query.category}
          onChange={(event) => { onChange({ category: event.target.value }) }}
          className={fieldClass}
        >
          <option value="">All categories</option>
          {categories.map((category) => (
            <option key={category} value={category}>
              {category}
            </option>
          ))}
        </select>
      </div>
    </div>
  )
}
