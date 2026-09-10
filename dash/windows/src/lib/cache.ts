/// Per (period, provider) payload cache. Entries are served instantly on tab switches and
/// refreshed in the background (stale-while-revalidate); `age` lets the caller decide
/// whether a background refresh is due.

interface CacheEntry<T> {
  data: T
  ts: number
}

export class PayloadCache<T> {
  private store = new Map<string, CacheEntry<T>>()
  private flights = new Set<string>()

  private key(period: string, provider: string): string {
    return `${period}:${provider}`
  }

  get(period: string, provider: string): T | null {
    return this.store.get(this.key(period, provider))?.data ?? null
  }

  /// Milliseconds since the entry was stored, or Infinity when absent.
  age(period: string, provider: string): number {
    const entry = this.store.get(this.key(period, provider))
    return entry ? Date.now() - entry.ts : Number.POSITIVE_INFINITY
  }

  set(period: string, provider: string, data: T): void {
    this.store.set(this.key(period, provider), { data, ts: Date.now() })
  }

  isInFlight(period: string, provider: string): boolean {
    return this.flights.has(this.key(period, provider))
  }

  markInFlight(period: string, provider: string): void {
    this.flights.add(this.key(period, provider))
  }

  clearInFlight(period: string, provider: string): void {
    this.flights.delete(this.key(period, provider))
  }
}
