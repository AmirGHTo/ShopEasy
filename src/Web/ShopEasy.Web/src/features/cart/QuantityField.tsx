import { useState } from 'react'
import { MAX_QUANTITY } from './cart'

interface QuantityFieldProps {
  id: string
  productName: string
  quantity: number
  onChange: (quantity: number) => void
}

// A number field that keeps up with the cart. While the visitor is typing, what is in the field is
// theirs (an empty field or a "0" on the way to "10" must not be corrected under their fingers); as
// soon as it holds a whole number the cart takes it, and the field shows what the cart made of it
// (150 becomes 100).
export default function QuantityField({ id, productName, quantity, onChange }: QuantityFieldProps) {
  const [draft, setDraft] = useState<string | null>(null)

  return (
    <div className="flex items-center gap-2">
      <label htmlFor={id} className="text-sm text-slate-700">
        Quantity{' '}
        <span className="sr-only">of {productName}</span>
      </label>
      <input
        id={id}
        type="number"
        inputMode="numeric"
        min={1}
        max={MAX_QUANTITY}
        step={1}
        value={draft ?? String(quantity)}
        onChange={(event) => {
          const typed = event.target.valueAsNumber
          if (typed >= 1) {
            setDraft(null)
            onChange(typed)
          } else {
            setDraft(event.target.value)
          }
        }}
        onBlur={() => { setDraft(null) }}
        className="w-20 rounded-lg border border-slate-300 px-2 py-1.5 text-sm focus:border-indigo-500 focus:outline-none focus:ring-2 focus:ring-indigo-500"
      />
    </div>
  )
}
