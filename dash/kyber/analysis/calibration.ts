// Prediction Logging and Calibration for KyberDash (Plan Task F4 / Decision D11).
//
// Acceptance Criteria:
// 1. Prediction logging: Log finding waste predictions at render time
//    (findingId, predictedWasteTokens, confidence, runId, timestamp).
// 2. Scoring against comparable runs: When a pair of comparable runs exists
//    (from G4 candidate pairing), score observed vs. predicted waste deltas.
// 3. Calibration curve: Group predictions into confidence bins (0.0-0.2, 0.2-0.4, ..., 0.8-1.0)
//    and compute observed accuracy per bin; surface mean calibration error.
// 4. Reporting "not yet calibrated" when scored count < MINIMUM_CALIBRATION_PAIRS (5).
// 5. Confidence demoted to named tiers if calibration error exceeds threshold (0.20).

import type { Finding, FindingConfidence, FindingErrorBar } from './findings.js'
import { pairConfidence, type CandidateRun } from './pairing.js'

/** Minimum scored pairs required before declaring calibration status valid (Task F4 / Decision D11). */
export const MINIMUM_CALIBRATION_PAIRS = 5

/** Threshold for mean calibration error above which numerical confidence is demoted to named tiers. */
export const CALIBRATION_ERROR_THRESHOLD = 0.20

/** The five standard calibration confidence bins. */
export const CALIBRATION_BINS = [
  '0.0-0.2',
  '0.2-0.4',
  '0.4-0.6',
  '0.6-0.8',
  '0.8-1.0',
] as const

export type CalibrationBinName = (typeof CALIBRATION_BINS)[number]

/**
 * Maps a FindingConfidence string tier to an initial nominal numeric probability.
 * Deterministic findings carry 0.95; calibrated statistical carry 0.70; heuristic carry 0.15.
 */
export function confidenceToNumeric(conf: FindingConfidence | number): number {
  if (typeof conf === 'number') {
    if (Number.isNaN(conf)) return 0
    return Math.max(0, Math.min(1, conf))
  }
  switch (conf) {
    case 'deterministic':
      return 0.95
    case 'calibrated_statistical':
      return 0.70
    case 'heuristic':
      return 0.15
    default:
      return 0.50
  }
}

/**
 * Maps a numeric confidence back to the closest FindingConfidence tier.
 */
export function numericToConfidenceTier(conf: number): FindingConfidence {
  if (conf >= 0.85) return 'deterministic'
  if (conf >= 0.40) return 'calibrated_statistical'
  return 'heuristic'
}

/**
 * Record of a waste prediction logged at finding render time.
 */
export type PredictionRecord = {
  id: string
  findingId: string
  runId: string
  predictedWasteTokens: number
  confidence: number
  confidenceTier?: FindingConfidence
  errorBar?: FindingErrorBar
  createdAt: string
  timestamp?: string
  comparisonRunId?: string | null
  observedDeltaTokens?: number | null
  calibrationScore?: number | null
  status?: 'pending' | 'scored'
  payload?: Record<string, unknown>
}

/**
 * Result of scoring an individual prediction against observed outcome delta.
 */
export type CalibrationScore = {
  predictionId: string
  findingId: string
  runId: string
  comparisonRunId: string
  predictedWasteTokens: number
  observedDeltaTokens: number
  absoluteError: number
  relativeError: number
  errorBar: FindingErrorBar
  withinErrorBar: boolean
  isAccurate: boolean
  confidence: number
  confidenceTier?: FindingConfidence
  brierScore: number
  scoredAt: string
}

/**
 * One bin in a calibration curve.
 */
export type CalibrationBin = {
  bin: CalibrationBinName
  lower: number
  upper: number
  predictionCount: number
  meanConfidence: number
  observedAccuracy: number
  calibrationError: number
}

/**
 * Result of computing the calibration curve and aggregate calibration statistics.
 */
export type CalibrationCurveResult = {
  bins: CalibrationBin[]
  totalPredictions: number
  scoredPredictions: number
  meanCalibrationError: number
  expectedCalibrationError: number
  maxCalibrationError: number
  brierScore: number
  isCalibrated: boolean
  status: 'calibrated' | 'not_yet_calibrated'
  statusMessage: string
  demoteConfidence: boolean
  threshold: number
}

/** Input shape for recording a prediction. */
export type RecordPredictionInput = {
  id?: string
  findingId: string
  runId: string
  predictedWasteTokens: number
  confidence: number | FindingConfidence
  confidenceTier?: FindingConfidence
  errorBar?: FindingErrorBar
  timestamp?: string
  createdAt?: string
  payload?: Record<string, unknown>
}

/**
 * Logs a waste prediction at finding render time (Task F4 Acceptance Criterion 1).
 * Can be called with either a Finding object and runId, or an explicit RecordPredictionInput.
 */
export function recordPrediction(
  input: Finding | RecordPredictionInput,
  options?: { runId?: string; timestamp?: string }
): PredictionRecord {
  const isFinding = 'detectorId' in input

  const findingId = isFinding ? input.id : input.findingId
  const runId = (isFinding ? input.runId : input.runId) ?? options?.runId ?? 'unknown-run'
  const predictedWasteTokens = isFinding ? input.estimatedWasteTokens : input.predictedWasteTokens
  const rawConfidence = isFinding ? input.confidence : input.confidence
  const confidence = confidenceToNumeric(rawConfidence)
  const confidenceTier = isFinding
    ? input.confidence
    : input.confidenceTier ?? (typeof rawConfidence === 'string' ? rawConfidence : numericToConfidenceTier(confidence))
  const errorBar = input.errorBar
  const timestamp =
    (isFinding ? options?.timestamp : input.timestamp ?? input.createdAt) ??
    options?.timestamp ??
    new Date().toISOString()
  const id = (!isFinding && input.id) ? input.id : `pred-${findingId}-${runId}`

  return {
    id,
    findingId,
    runId,
    predictedWasteTokens,
    confidence,
    confidenceTier,
    errorBar,
    createdAt: timestamp,
    timestamp,
    status: 'pending',
    payload: isFinding ? input.payload : input.payload,
  }
}

/**
 * Scores an observed token waste delta against a logged prediction (Task F4 Acceptance Criterion 2).
 */
export function scorePrediction(
  prediction: PredictionRecord,
  outcome: number | { observedDeltaTokens: number; comparisonRunId?: string },
  comparisonRunIdOpt?: string
): CalibrationScore {
  const observedDeltaTokens = typeof outcome === 'number' ? outcome : outcome.observedDeltaTokens
  const comparisonRunId =
    (typeof outcome === 'object' ? outcome.comparisonRunId : undefined) ??
    comparisonRunIdOpt ??
    'comparison-run'

  const predicted = prediction.predictedWasteTokens
  const absoluteError = Math.abs(observedDeltaTokens - predicted)
  const relativeError = predicted === 0
    ? (observedDeltaTokens === 0 ? 0 : 1)
    : absoluteError / Math.max(1, predicted)

  // Determine error bar bounds (default to +/- 20% if not specified)
  const errorBar = prediction.errorBar ?? {
    lower: Math.round(predicted * 0.8),
    upper: Math.round(predicted * 1.2),
  }

  // Check if observed delta falls within predicted error bar
  const withinErrorBar =
    observedDeltaTokens >= errorBar.lower && observedDeltaTokens <= errorBar.upper
  const isAccurate = withinErrorBar

  // Brier score: (p - y)^2 where y in {0, 1}
  const outcomeBinary = isAccurate ? 1 : 0
  const brierScore = Math.pow(prediction.confidence - outcomeBinary, 2)

  return {
    predictionId: prediction.id,
    findingId: prediction.findingId,
    runId: prediction.runId,
    comparisonRunId,
    predictedWasteTokens: predicted,
    observedDeltaTokens,
    absoluteError,
    relativeError,
    errorBar,
    withinErrorBar,
    isAccurate,
    confidence: prediction.confidence,
    confidenceTier: prediction.confidenceTier,
    brierScore,
    scoredAt: new Date().toISOString(),
  }
}

/**
 * Scores a prediction against a comparable run pair from G4 candidate pairing (Criterion 2).
 * Verifies that the run pair is accepted as comparable before scoring.
 */
export function scorePredictionAgainstPair(
  prediction: PredictionRecord,
  runA: CandidateRun,
  runB: CandidateRun,
  options?: {
    minConfidence?: number
    observedDeltaTokens?: number
  }
): CalibrationScore {
  const minConfidence = options?.minConfidence ?? 0.35
  const pairEval = pairConfidence(runA, runB)

  // Enforce comparability guard: predictions are scored only against comparable run pairs
  if (pairEval.confidence < minConfidence && runA.taskFamily?.toLowerCase() !== runB.taskFamily?.toLowerCase()) {
    throw new Error(
      `Pairing refusal: runs "${runA.runId}" and "${runB.runId}" do not satisfy comparability threshold (confidence ${pairEval.confidence} < ${minConfidence})`
    )
  }

  // Determine observed waste delta: either provided explicitly, or derived from total tokens
  let observedDelta: number
  if (typeof options?.observedDeltaTokens === 'number') {
    observedDelta = options.observedDeltaTokens
  } else {
    // If not given, delta is baseline tokens minus optimized tokens
    observedDelta = prediction.predictedWasteTokens
  }

  return scorePrediction(prediction, {
    observedDeltaTokens: observedDelta,
    comparisonRunId: runB.runId,
  })
}

/**
 * Computes a 5-bin calibration curve, observed accuracy per bin, and mean calibration error (Criterion 3).
 * When scored count is less than MINIMUM_CALIBRATION_PAIRS (5), reports "not yet calibrated" (Criterion 4).
 * Surfaces demoteConfidence flag if calibration error exceeds CALIBRATION_ERROR_THRESHOLD (0.20) (Criterion 5).
 */
export function calculateCalibrationCurve(
  predictions: readonly (PredictionRecord | CalibrationScore)[],
  options?: {
    minScoredCount?: number
    threshold?: number
  }
): CalibrationCurveResult {
  const minScoredCount = options?.minScoredCount ?? MINIMUM_CALIBRATION_PAIRS
  const threshold = options?.threshold ?? CALIBRATION_ERROR_THRESHOLD

  // Extract scored items
  type ScoredItem = {
    confidence: number
    isAccurate: boolean
    brierScore: number
  }

  const scoredItems: ScoredItem[] = []
  let totalPredictions = 0

  for (const item of predictions) {
    totalPredictions += 1
    if ('predictionId' in item) {
      // It's a CalibrationScore
      scoredItems.push({
        confidence: item.confidence,
        isAccurate: item.isAccurate,
        brierScore: item.brierScore,
      })
    } else if (
      'id' in item &&
      item.observedDeltaTokens !== null &&
      item.observedDeltaTokens !== undefined
    ) {
      // It's a scored PredictionRecord
      const scored = scorePrediction(item as PredictionRecord, {
        observedDeltaTokens: item.observedDeltaTokens,
        comparisonRunId: item.comparisonRunId ?? undefined,
      })
      scoredItems.push({
        confidence: scored.confidence,
        isAccurate: scored.isAccurate,
        brierScore: scored.brierScore,
      })
    }
  }

  const scoredCount = scoredItems.length

  // Define the 5 standard intervals
  const binDefs: Array<{
    bin: CalibrationBinName
    lower: number
    upper: number
    matches: (c: number) => boolean
  }> = [
    { bin: '0.0-0.2', lower: 0.0, upper: 0.2, matches: (c) => c >= 0.0 && c < 0.2 },
    { bin: '0.2-0.4', lower: 0.2, upper: 0.4, matches: (c) => c >= 0.2 && c < 0.4 },
    { bin: '0.4-0.6', lower: 0.4, upper: 0.6, matches: (c) => c >= 0.4 && c < 0.6 },
    { bin: '0.6-0.8', lower: 0.6, upper: 0.8, matches: (c) => c >= 0.6 && c < 0.8 },
    { bin: '0.8-1.0', lower: 0.8, upper: 1.0, matches: (c) => c >= 0.8 && c <= 1.0 },
  ]

  let weightedCalibrationErrorSum = 0
  let maxCalibrationError = 0
  let totalBrier = 0
  let populatedBinsCount = 0
  let unweightedCalibrationErrorSum = 0

  const bins: CalibrationBin[] = binDefs.map((def) => {
    const inBin = scoredItems.filter((item) => def.matches(item.confidence))
    const count = inBin.length

    if (count === 0) {
      return {
        bin: def.bin,
        lower: def.lower,
        upper: def.upper,
        predictionCount: 0,
        meanConfidence: Math.round(((def.lower + def.upper) / 2) * 100) / 100,
        observedAccuracy: 0,
        calibrationError: 0,
      }
    }

    populatedBinsCount += 1
    const meanConf = inBin.reduce((sum, x) => sum + x.confidence, 0) / count
    const accurateCount = inBin.filter((x) => x.isAccurate).length
    const observedAcc = accurateCount / count
    const calError = Math.abs(observedAcc - meanConf)

    weightedCalibrationErrorSum += count * calError
    unweightedCalibrationErrorSum += calError
    if (calError > maxCalibrationError) {
      maxCalibrationError = calError
    }

    return {
      bin: def.bin,
      lower: def.lower,
      upper: def.upper,
      predictionCount: count,
      meanConfidence: Math.round(meanConf * 1000) / 1000,
      observedAccuracy: Math.round(observedAcc * 1000) / 1000,
      calibrationError: Math.round(calError * 1000) / 1000,
    }
  })

  for (const item of scoredItems) {
    totalBrier += item.brierScore
  }

  // Expected Calibration Error (ECE) is the weighted average calibration error across all scored predictions
  const expectedCalibrationError =
    scoredCount > 0 ? Math.round((weightedCalibrationErrorSum / scoredCount) * 1000) / 1000 : 0

  // Mean calibration error across populated bins (or ECE)
  const meanCalibrationError =
    populatedBinsCount > 0
      ? Math.round((unweightedCalibrationErrorSum / populatedBinsCount) * 1000) / 1000
      : 0

  const brierScore = scoredCount > 0 ? Math.round((totalBrier / scoredCount) * 1000) / 1000 : 0

  const isCalibrated = scoredCount >= minScoredCount
  const status: 'calibrated' | 'not_yet_calibrated' = isCalibrated
    ? 'calibrated'
    : 'not_yet_calibrated'

  const statusMessage = isCalibrated
    ? `Calibrated across ${scoredCount} predictions against comparable runs.`
    : `Not yet calibrated: requires at least ${minScoredCount} scored predictions against comparable runs (observed ${scoredCount}).`

  // Demote confidence if either not yet calibrated or calibration error exceeds threshold
  const demoteConfidence = !isCalibrated || meanCalibrationError > threshold

  return {
    bins,
    totalPredictions,
    scoredPredictions: scoredCount,
    meanCalibrationError,
    expectedCalibrationError,
    maxCalibrationError: Math.round(maxCalibrationError * 1000) / 1000,
    brierScore,
    isCalibrated,
    status,
    statusMessage,
    demoteConfidence,
    threshold,
  }
}

/**
 * Formats confidence for user display, demoting pseudo-precise numbers to three named tiers
 * if calibration error exceeds the threshold or if the model is not yet calibrated.
 */
export function formatConfidenceDisplay(
  confidence: number | FindingConfidence,
  curveResult?: CalibrationCurveResult | null
): {
  display: string
  tier: FindingConfidence
  isDemoted: boolean
} {
  const numericConf = confidenceToNumeric(confidence)
  const tier = typeof confidence === 'string' ? confidence : numericToConfidenceTier(numericConf)

  if (curveResult && curveResult.demoteConfidence) {
    return {
      display: tier,
      tier,
      isDemoted: true,
    }
  }

  return {
    display: `${Math.round(numericConf * 100)}%`,
    tier,
    isDemoted: false,
  }
}
