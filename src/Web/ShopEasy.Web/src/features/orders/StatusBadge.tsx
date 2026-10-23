const BADGES: Record<string, { label: string; className: string }> = {
  Pending: { label: 'Pending', className: 'bg-amber-100 text-amber-800' },
  Confirmed: { label: 'Confirmed', className: 'bg-green-100 text-green-800' },
  Shipped: { label: 'Shipped', className: 'bg-green-100 text-green-800' },
  Delivered: { label: 'Delivered', className: 'bg-green-100 text-green-800' },
  Cancelled: { label: 'Cancelled', className: 'bg-slate-200 text-slate-700' },
  PaymentFailed: { label: 'Payment failed', className: 'bg-red-100 text-red-800' },
}

// The status of an order in a word, not in colour alone. A status the shop adds later is shown as it is.
export default function StatusBadge({ statusName }: { statusName: string }) {
  const badge = BADGES[statusName] ?? { label: statusName, className: 'bg-slate-100 text-slate-700' }
  return <span className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${badge.className}`}>{badge.label}</span>
}
