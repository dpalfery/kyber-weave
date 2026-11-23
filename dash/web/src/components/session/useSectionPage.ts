import { useState } from 'react'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import {
  kyberSessionSectionQueryKey,
  type KyberSectionWindow,
  type KyberSessionSection,
} from '../../lib/kyberApi.js'

/**
 * One section's paged list: the page the viewer is on, and the query that
 * reads it. Every paged section of the session view reads through this hook,
 * so the query key, the previous-page hold and the window fields never drift
 * between them. The section owns exactly this one query.
 */
export interface SectionPageState<T> {
  page: number
  setPage: (page: number) => void
  data: T | undefined
  /** True while a page beyond the first is in flight; the first page has nothing to keep. */
  isFetching: boolean
  isError: boolean
  error: unknown
  refetch: () => void
}

export function useSectionPage<T extends KyberSectionWindow>(
  sessionId: string,
  section: KyberSessionSection,
  fetchPage: (sessionId: string, page: number) => Promise<T>,
): SectionPageState<T> {
  const [page, setPage] = useState(1)
  const query = useQuery({
    queryKey: kyberSessionSectionQueryKey(sessionId, section, page),
    queryFn: () => fetchPage(sessionId, page),
    // The previous page stays on screen while the next one loads: the pager
    // disables instead, so a page turn never blanks a list the viewer was
    // reading.
    placeholderData: keepPreviousData,
  })
  return {
    page,
    setPage,
    data: query.data,
    isFetching: query.isFetching,
    isError: query.isError,
    error: query.error,
    refetch: () => void query.refetch(),
  }
}

/** The message one section's failed route states, in the words it failed with. */
export function sectionErrorMessage(error: unknown): string {
  if (error instanceof Error) return error.message
  return String(error)
}
