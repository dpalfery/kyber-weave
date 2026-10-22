// Store-generation polling (issue #319, architecture rule R1).
//
// The dashboard is a display layer, but a tab that never re-reads shows stale
// counts with no indication why: a clean, a manual import, or a `kyberdash
// refresh` triggered from another surface all rewrite the store underneath the
// open tab. Nothing here schedules a job — the JobHost does. This hook only
// notices that the ground moved.
//
// The contract is deliberately narrow, because a wider one costs more than it
// buys:
//   - poll `GET /api/kyber/jobs` (through the shared kyberApi client, never a
//     raw fetch) roughly every 10 seconds, and nothing else — a poll that
//     fanned out into the data endpoints would cause exactly the refetch churn
//     this exists to avoid;
//   - invalidate the react-query cache ONLY when `storeGeneration` differs from
//     the last value observed, so a quiet host costs one cheap read a minute
//     and no refetches;
//   - a FAILED poll observes nothing. Treating it as "generation 0" would read
//     as a wiped store and invalidate every query on the page, so the last
//     observed value is kept and the next successful poll decides.

import { useEffect, useRef } from 'react'
import { useQueryClient } from '@tanstack/react-query'

import { fetchKyberJobs } from './kyberApi.js'

/** How often the job status is read. Matches the host's own tick order of magnitude. */
export const STORE_GENERATION_POLL_MS = 10_000

/**
 * Invalidates the query cache when the server's store generation changes.
 *
 * <remarks>
 * Mount it once, inside the `QueryClientProvider` — a second mount would poll
 * the same endpoint twice and could invalidate from a stale baseline.
 * </remarks>
 */
export function useStoreGeneration(): void {
  const queryClient = useQueryClient()
  // A ref, not state: the observed generation is not rendered anywhere, and
  // writing state from a poll would re-render the tree every ten seconds.
  const observed = useRef<number | null>(null)

  useEffect(() => {
    let cancelled = false

    const poll = (): void => {
      void fetchKyberJobs()
        .then((status) => {
          if (cancelled) return
          const generation = status.storeGeneration
          const previous = observed.current
          observed.current = generation
          // The baseline poll only records; there is nothing to invalidate
          // against yet, and invalidating on mount would refetch the world.
          if (previous === null || previous === generation) return
          void queryClient.invalidateQueries()
        })
        .catch(() => {
          // Deliberately empty: an unreadable status reports no generation, so
          // the last observed value stands (see the file header).
        })
    }

    poll()
    const timer = setInterval(poll, STORE_GENERATION_POLL_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [queryClient])
}
