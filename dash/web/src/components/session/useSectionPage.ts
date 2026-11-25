import { useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import {
  KYBER_SECTION_PAGE_LIMIT,
  KYBER_SECTION_PAGE_LIMITS,
  KYBER_SECTION_PAGE_ORDER,
  kyberSessionSectionQueryKey,
  type KyberSectionPageLimit,
  type KyberSectionPageOrder,
  type KyberSectionWindow,
  type KyberSessionSection,
} from '../../lib/kyberApi.js'

const SECTION_LIMIT_STORAGE_PREFIX = 'kyberdash:session-section:limit:'

function isSectionPageLimit(value: number): value is KyberSectionPageLimit {
  return value === 25 || value === 50 || value === 100
}

function readStoredSectionLimit(section: KyberSessionSection): KyberSectionPageLimit {
  try {
    const storage = typeof window !== 'undefined' ? window.sessionStorage : undefined
    if (!storage) return KYBER_SECTION_PAGE_LIMIT
    const raw = storage.getItem(`${SECTION_LIMIT_STORAGE_PREFIX}${section}`)
    if (raw === '25') return 25
    if (raw === '50') return 50
    if (raw === '100') return 100
    return KYBER_SECTION_PAGE_LIMIT
  } catch {
    // Restricted browser contexts deny sessionStorage access; fall back to the default page size.
    return KYBER_SECTION_PAGE_LIMIT
  }
}

function writeStoredSectionLimit(section: KyberSessionSection, limit: KyberSectionPageLimit): void {
  try {
    const storage = typeof window !== 'undefined' ? window.sessionStorage : undefined
    storage?.setItem(`${SECTION_LIMIT_STORAGE_PREFIX}${section}`, String(limit))
  } catch {
    // Restricted browser contexts deny sessionStorage writes; keep the in-memory page size.
  }
}

/**
 * One section's paged list: the page, page size and sort order the viewer is on,
 * and the query that reads it. Every paged section of the session view reads
 * through this hook, so the query key, the previous-page hold and the window
 * fields never drift between them. The section owns exactly this one query.
 */
export interface SectionPageState<T> {
  page: number
  setPage: (page: number) => void
  limit: KyberSectionPageLimit
  setLimit: (limit: KyberSectionPageLimit) => void
  order: KyberSectionPageOrder
  setOrder: (order: KyberSectionPageOrder) => void
  data: T | undefined
  /** True while a page beyond the first is in flight; the first page has nothing to keep. */
  isFetching: boolean
  isError: boolean
  error: unknown
  refetch: () => void
  /**
   * The failure to state, when the route failed or its payload names a faulted
   * projection; null while the section can render. A payload whose projection
   * read faulted is this section's error state — never an empty list, never a zero.
   */
  failure: string | null
  /** Ready-to-spread props for the shared Pager control. */
  pagerProps: {
    page: number
    limit: KyberSectionPageLimit
    total: number
    onPageChange: (page: number) => void
    limitOptions: readonly KyberSectionPageLimit[]
    onLimitChange: (limit: KyberSectionPageLimit) => void
    order: KyberSectionPageOrder
    onOrderChange: (order: KyberSectionPageOrder) => void
    loading: boolean
  }
}

/** The status a section payload carries when its session's projection read faulted. */
export const PROJECTION_FAILED_STATUS = 'PROJECTION_FAILED'

/**
 * The message a section states when its payload names the session's projection
 * faulted: the route answered, but the answer is not data. Null when the payload
 * carries no such fault, or is not an object a fault can be read off.
 */
export function projectionFailureMessage(data: unknown): string | null {
  if (
    data !== null &&
    typeof data === 'object' &&
    (data as { projection_status?: unknown }).projection_status === PROJECTION_FAILED_STATUS
  ) {
    return 'The stored projection for this session failed to build (PROJECTION_FAILED), so this section has no data to serve. Re-project the session to restore it.'
  }
  return null
}

export function useSectionPage<T extends KyberSectionWindow>(
  sessionId: string,
  section: KyberSessionSection,
  fetchPage: (
    sessionId: string,
    page: number,
    limit: KyberSectionPageLimit,
    order: KyberSectionPageOrder,
  ) => Promise<T>,
): SectionPageState<T> {
  // A session change restarts the section at its first page: page N of the
  // previous session is not where a viewer opening another session is. Adjusted
  // during render (the React state-when-props-change pattern), so the request
  // for the previous session's page never leaves with the new session's id.
  const [prevSessionId, setPrevSessionId] = useState(sessionId)
  const [prevSection, setPrevSection] = useState(section)
  const [page, setPage] = useState(1)
  const [limit, setLimitState] = useState<KyberSectionPageLimit>(() => readStoredSectionLimit(section))
  const [order, setOrderState] = useState<KyberSectionPageOrder>(KYBER_SECTION_PAGE_ORDER)
  if (prevSessionId !== sessionId || prevSection !== section) {
    setPrevSessionId(sessionId)
    if (prevSection !== section) {
      setPrevSection(section)
      setLimitState(readStoredSectionLimit(section))
    }
    setPage(1)
  }

  const setLimit = (nextLimit: KyberSectionPageLimit) => {
    if (!isSectionPageLimit(nextLimit)) return
    writeStoredSectionLimit(section, nextLimit)
    setLimitState(nextLimit)
    setPage(1)
  }

  const setOrder = (nextOrder: KyberSectionPageOrder) => {
    setOrderState(nextOrder)
    setPage(1)
  }

  const query = useQuery({
    queryKey: kyberSessionSectionQueryKey(sessionId, section, page, limit, order),
    queryFn: () => fetchPage(sessionId, page, limit, order),
    // The previous page stays on screen while the next one loads: the pager
    // disables instead, so a page turn never blanks a list the viewer was
    // reading.
    placeholderData: keepPreviousData,
  })
  const failure =
    projectionFailureMessage(query.data) ??
    (query.isError && query.data === undefined ? sectionErrorMessage(query.error) : null)
  return {
    page,
    setPage,
    limit,
    setLimit,
    order,
    setOrder,
    data: query.data,
    isFetching: query.isFetching,
    isError: query.isError,
    error: query.error,
    refetch: () => void query.refetch(),
    failure,
    pagerProps: {
      page,
      limit,
      total: query.data?.total ?? 0,
      onPageChange: setPage,
      limitOptions: KYBER_SECTION_PAGE_LIMITS,
      onLimitChange: setLimit,
      order,
      onOrderChange: setOrder,
      loading: query.isFetching,
    },
  }
}

/** The message one section's failed route states, in the words it failed with. */
export function sectionErrorMessage(error: unknown): string {
  if (error instanceof Error) return error.message
  return String(error)
}
