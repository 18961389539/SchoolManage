/**
 * Guards a page against out-of-order responses. Each load takes a token; only the newest
 * token may write items, errors or loading state back into the page.
 */
export interface RequestGeneration {
  begin(): number
  isCurrent(token: number): boolean
}

export function useRequestGeneration(): RequestGeneration {
  let current = 0
  return {
    begin: () => ++current,
    isCurrent: (token: number) => token === current
  }
}
