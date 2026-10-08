import { Assertion } from 'chai'

// The refresh contract asserts `toBeDisabled` / `not.toBeDisabled`. That suite
// does not load jest-dom, so the chai method is registered here — a Vitest
// setup file — rather than in the shipped ContextDoctor module.
declare module 'vitest' {
  // `T` is vitest's subject parameter. The conditional keeps it in the
  // signature so the augmentation merges, and still returns `R`.
  interface Matchers<R extends void | Promise<void> = void | Promise<void>, T = unknown> {
    toBeDisabled(): [R, T] extends [R, T] ? R : never
  }
}

Assertion.addMethod('toBeDisabled', function (this: Chai.AssertionStatic) {
  const element = this._obj as { disabled?: boolean } | null
  const disabled = element != null && element.disabled === true
  this.assert(disabled, 'expected #{this} to be disabled', 'expected #{this} not to be disabled')
})
