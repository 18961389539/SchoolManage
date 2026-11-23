/**
 * Keyboard affordances for list-heavy screens. A plain `/` focuses the search field of the
 * current page, mirroring the convention used by most admin consoles. Typing is never
 * interrupted: the shortcut is ignored while a form control has focus or a dialog is open.
 */

const SEARCH_SELECTORS = [
  'input[aria-label*="搜索"]',
  'input[placeholder*="搜索"]',
  'input[placeholder*="查找"]',
  'input[type="search"]'
]

interface SearchField {
  input: HTMLInputElement
  clear: () => void
}

function isTyping(target: EventTarget | null): boolean {
  const element = target as HTMLElement | null
  if (!element) return false
  if (/^(input|textarea|select)$/i.test(element.tagName)) return true
  return element.isContentEditable === true
}

function dialogOpen(): boolean {
  return [...document.querySelectorAll('.el-overlay, .el-overlay-message-box')].some(
    element =>
      element.getClientRects().length > 0 &&
      getComputedStyle(element).display !== 'none' &&
      getComputedStyle(element).visibility !== 'hidden'
  )
}

function visible(element: HTMLElement): boolean {
  const style = getComputedStyle(element)
  const disabled = 'disabled' in element && element.disabled === true
  return element.getClientRects().length > 0 && style.display !== 'none' && style.visibility !== 'hidden' && !disabled
}

function findSearchField(): SearchField | null {
  // Scope to the routed page first: the sidebar also has a search box, but Ctrl+K owns that one.
  const roots = [document.querySelector('#main-content'), document.querySelector('.app-layout'), document]
  for (const root of roots) {
    if (!root) continue
    for (const selector of SEARCH_SELECTORS) {
      for (const candidate of root.querySelectorAll<HTMLInputElement>(selector)) {
        if (!visible(candidate)) continue
        return { input: candidate, clear: clearActionFor(candidate) }
      }
    }
  }
  return null
}

function clearActionFor(input: HTMLInputElement): () => void {
  const wrapper = input.closest('.el-input')
  const clearButton = wrapper?.querySelector<HTMLElement>('.el-input__clear')
  if (clearButton && visible(clearButton)) return () => clearButton.click()
  return () => {
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set
    setter?.call(input, '')
    input.dispatchEvent(new Event('input', { bubbles: true }))
  }
}

function focusSearch(): void {
  if (dialogOpen()) return
  const field = findSearchField()
  if (!field) return
  field.input.focus()
  field.input.select()
}

function clearActiveSearch(target: HTMLElement): void {
  if (dialogOpen() || !/^input$/i.test(target.tagName)) return
  const input = target as HTMLInputElement
  const field = findSearchField()
  if (!field || field.input !== input || !input.value) return
  // Prefer Element Plus's own clear icon; fall back to a native input event so pages whose
  // clear icon is rendered only on hover still respond.
  field.clear?.()
}

document.addEventListener('keydown', event => {
  if (event.key === '/' && !event.ctrlKey && !event.metaKey && !event.altKey && !isTyping(event.target)) {
    if (dialogOpen()) return
    event.preventDefault()
    focusSearch()
    return
  }
  if (event.key === 'Escape' && event.target) clearActiveSearch(event.target as HTMLElement)
})
