const emergencyFlushes = new Set<() => void>()

export function registerEmergencyFlush(flush: () => void): () => void {
  emergencyFlushes.add(flush)
  return () => emergencyFlushes.delete(flush)
}

export function flushEmergencyDrafts() {
  for (const flush of emergencyFlushes) {
    try {
      flush()
    } catch {
      // A draft hook must never prevent the session from being cleared.
    }
  }
}

export function readDraft<T>(key: string): T | null {
  try {
    const value = localStorage.getItem(key)
    return value ? (JSON.parse(value) as T) : null
  } catch {
    return null
  }
}

export function writeDraft(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value))
  } catch {
    // Storage can be unavailable in private or restricted browser contexts.
  }
}

export function clearDraft(key: string) {
  try {
    localStorage.removeItem(key)
  } catch {
    // Nothing else can be done when storage is unavailable.
  }
}
