import axe from 'axe-core'
import { expect } from 'vitest'

// Runs the axe accessibility rules on what a test has rendered (names for links and buttons, valid
// roles and landmarks, ...). Colour contrast needs a real browser and is left to the browser test.
export async function expectNoAccessibilityViolations(container: HTMLElement): Promise<void> {
  const { violations } = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })

  const found = violations.map((v) => `${v.id}: ${v.help} (${v.nodes.map((n) => n.target.join(' ')).join(', ')})`)
  expect(found).toEqual([])
}
