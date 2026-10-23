import type { OrderProblem } from './orderProblem'

// Shows what went wrong when placing the order: what happened, what the shop said, and how long to
// wait when it asked for that. role="alert" so that a screen reader says it at once.
export default function OrderProblemNotice({ problem }: { problem: OrderProblem }) {
  return (
    <div role="alert" className="flex flex-col gap-2 rounded-xl border border-red-200 bg-red-50 p-4 text-red-900">
      <p className="font-medium">{problem.title}</p>
      {problem.details.length > 0 && (
        <ul className="list-inside list-disc text-sm">
          {problem.details.map((detail) => (
            <li key={detail}>{detail}</li>
          ))}
        </ul>
      )}
      {problem.retryAfterSeconds !== undefined && (
        <p className="text-sm">Please wait {problem.retryAfterSeconds} seconds, then try again.</p>
      )}
    </div>
  )
}
