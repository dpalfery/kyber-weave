import { Dropdown } from 'codeburn-desktop'

const MODELS = [
  { value: 'claude-opus-5', label: 'claude-opus-5 · 1,204 calls' },
  { value: 'claude-sonnet-5', label: 'claude-sonnet-5 · 8,930 calls' },
  { value: 'claude-haiku-4-5', label: 'claude-haiku-4.5 · 21,447 calls' },
]

const PERIODS = [
  { value: 'today', label: 'Today' },
  { value: 'week', label: '7d' },
  { value: '30days', label: '30d' },
  { value: 'month', label: 'Month' },
  { value: 'all', label: 'All' },
]

/** The default trigger: current selection plus the chevron glyph. */
export function ModelPicker() {
  return (
    <Dropdown
      id="preview-model"
      ariaLabel="Model"
      value="claude-opus-5"
      options={MODELS}
      onChange={() => {}}
    />
  )
}

/** `width` pins the trigger for short option sets (the Settings idiom). */
export function FixedWidth() {
  return (
    <Dropdown
      id="preview-period"
      ariaLabel="Default period"
      value="30days"
      options={PERIODS}
      onChange={() => {}}
      width={92}
    />
  )
}

/** Several dropdowns read as one control strip in a settings row. */
export function ControlStrip() {
  return (
    <div style={{ display: 'flex', gap: 'var(--sp-2)', alignItems: 'center' }}>
      <Dropdown id="preview-scope" ariaLabel="Scope" value="combined"
        options={[{ value: 'local', label: 'Local' }, { value: 'combined', label: 'Combined' }]}
        onChange={() => {}} width={110} />
      <Dropdown id="preview-currency" ariaLabel="Currency" value="USD"
        options={[{ value: 'USD', label: 'USD' }, { value: 'EUR', label: 'EUR' }, { value: 'GBP', label: 'GBP' }]}
        onChange={() => {}} width={92} />
    </div>
  )
}
