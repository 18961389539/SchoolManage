export function focusFirstField(root?: ParentNode | null) {
  if (!root) return false
  const field = root.querySelector<HTMLElement>(
    'input:not([disabled]):not([type="hidden"]):not([type="file"]):not([tabindex="-1"]), textarea:not([disabled])'
  )
  field?.focus()
  return !!field
}
