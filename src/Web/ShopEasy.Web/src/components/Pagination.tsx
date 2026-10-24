interface PaginationProps {
  page: number
  totalPages: number
  onPageChange: (page: number) => void
}

const buttonClass =
  'rounded-lg border border-slate-300 bg-white px-3 py-1.5 text-sm font-medium text-slate-700 hover:bg-slate-50 disabled:cursor-not-allowed disabled:opacity-50'

export default function Pagination({ page, totalPages, onPageChange }: PaginationProps) {
  if (totalPages <= 1) return null

  return (
    <nav aria-label="Pagination" className="flex items-center justify-center gap-4">
      <button type="button" className={buttonClass} disabled={page <= 1} onClick={() => { onPageChange(page - 1) }}>
        Previous
      </button>
      <span className="text-sm text-slate-600">
        Page {page} of {totalPages}
      </span>
      <button
        type="button"
        className={buttonClass}
        disabled={page >= totalPages}
        onClick={() => { onPageChange(page + 1) }}
      >
        Next
      </button>
    </nav>
  )
}
