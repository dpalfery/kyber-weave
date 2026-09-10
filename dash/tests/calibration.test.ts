import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'
import { afterEach, describe, expect, it } from 'vitest'

import {
  calculateCalibrationCurve,
  CALIBRATION_BINS,
  CALIBRATION_ERROR_THRESHOLD,
  confidenceToNumeric,
  formatConfidenceDisplay,
  MINIMUM_CALIBRATION_PAIRS,
  numericToConfidenceTier,
  recordPrediction,
  scorePrediction,
  scorePredictionAgainstPair,
  type CalibrationScore,
  type PredictionRecord,
} from '../kyber/analysis/calibration.js'
import type { Finding } from '../kyber/analysis/findings.js'
import type { CandidateRun } from '../kyber/analysis/pairing.js'
import { CanonStore, SCHEMA_VERSION } from '../kyber/canon/store.js'
import { KyberBridge } from '../kyber/server/bridge.js'
import { handleKyberRequest } from '../kyber/server/routes.js'

const tempDirs: string[] = []

function tempStorePath(): string {
  const dir = mkdtempSync(join(tmpdir(), 'kyber-calibration-test-'))
  tempDirs.push(dir)
  return join(dir, 'canon.db')
}

afterEach(() => {
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop()!
    rmSync(dir, { recursive: true, force: true })
  }
})

// Sample finding generator
function createMockFinding(overrides: Partial<Finding> = {}): Finding {
  const waste = overrides.estimatedWasteTokens ?? 4000
  return {
    id: overrides.id ?? 'f-test-1',
    detectorId: overrides.detectorId ?? 'dormant-tool-schema',
    title: overrides.title ?? 'Dormant tool schema detected',
    mechanism: overrides.mechanism ?? 'Schema resident 5 turns without calls',
    evidenceLinks: overrides.evidenceLinks ?? [
      { spanId: 'span-1', turnIndex: 0, description: 'Declaration in turn 1' },
      { spanId: 'span-2', turnIndex: 4, description: 'Resident in turn 5' },
    ],
    confidence: overrides.confidence ?? 'deterministic',
    estimatedWasteTokens: waste,
    recommendation: overrides.recommendation ?? 'Relocate schema to on-demand progressive disclosure.',
    errorBar: overrides.errorBar ?? { lower: Math.round(waste * 0.8), upper: Math.round(waste * 1.2) },
    outcomeRiskCaveat: overrides.outcomeRiskCaveat ?? 'No outcome risk detected.',
    runId: overrides.runId ?? 'run-test-alpha',
    sessionId: overrides.sessionId ?? 'session-test-alpha',
    rankScore: overrides.rankScore ?? waste,
  }
}

// Sample candidate run generator
function createMockCandidateRun(overrides: Partial<CandidateRun> = {}): CandidateRun {
  return {
    runId: overrides.runId ?? 'run-candidate-1',
    harness: overrides.harness ?? 'claude-code',
    taskFamily: overrides.taskFamily ?? 'refactor-auth-service',
    workingDirectory: overrides.workingDirectory ?? '/repo/project',
    repo: overrides.repo ?? 'org/project',
    started: overrides.started ?? '2026-09-06T12:00:00.000Z',
    ended: overrides.ended ?? '2026-09-06T12:05:00.000Z',
    outcome: overrides.outcome ?? { status: 'success' },
    executionCount: overrides.executionCount ?? 1,
    turnCount: overrides.turnCount ?? 5,
  }
}

describe('Task F4: Prediction Logging (Criterion 1)', () => {
  it('logs finding waste predictions at render time from a Finding', () => {
    const finding = createMockFinding({
      id: 'finding-waste-101',
      runId: 'run-alpha',
      estimatedWasteTokens: 5200,
      confidence: 'deterministic',
      errorBar: { lower: 4800, upper: 5600 },
    })

    const timestamp = '2026-09-06T14:00:00.000Z'
    const prediction = recordPrediction(finding, { timestamp })

    expect(prediction.id).toBe('pred-finding-waste-101-run-alpha')
    expect(prediction.findingId).toBe('finding-waste-101')
    expect(prediction.runId).toBe('run-alpha')
    expect(prediction.predictedWasteTokens).toBe(5200)
    expect(prediction.confidence).toBe(0.95)
    expect(prediction.confidenceTier).toBe('deterministic')
    expect(prediction.errorBar).toEqual({ lower: 4800, upper: 5600 })
    expect(prediction.createdAt).toBe(timestamp)
    expect(prediction.timestamp).toBe(timestamp)
    expect(prediction.status).toBe('pending')
  })

  it('logs prediction with explicit RecordPredictionInput shape', () => {
    const prediction = recordPrediction({
      id: 'custom-pred-1',
      findingId: 'f-custom-1',
      runId: 'run-custom',
      predictedWasteTokens: 1200,
      confidence: 0.75,
      errorBar: { lower: 1000, upper: 1400 },
      timestamp: '2026-09-06T15:30:00.000Z',
    })

    expect(prediction.id).toBe('custom-pred-1')
    expect(prediction.findingId).toBe('f-custom-1')
    expect(prediction.runId).toBe('run-custom')
    expect(prediction.predictedWasteTokens).toBe(1200)
    expect(prediction.confidence).toBe(0.75)
    expect(prediction.confidenceTier).toBe('calibrated_statistical')
    expect(prediction.errorBar).toEqual({ lower: 1000, upper: 1400 })
    expect(prediction.createdAt).toBe('2026-09-06T15:30:00.000Z')
  })

  it('correctly maps confidence tiers to numeric confidence', () => {
    expect(confidenceToNumeric('deterministic')).toBe(0.95)
    expect(confidenceToNumeric('calibrated_statistical')).toBe(0.70)
    expect(confidenceToNumeric('heuristic')).toBe(0.15)
    expect(confidenceToNumeric(0.85)).toBe(0.85)
    expect(confidenceToNumeric(-0.5)).toBe(0.0)
    expect(confidenceToNumeric(1.5)).toBe(1.0)
  })

  it('correctly maps numeric confidence to confidence tiers', () => {
    expect(numericToConfidenceTier(0.95)).toBe('deterministic')
    expect(numericToConfidenceTier(0.85)).toBe('deterministic')
    expect(numericToConfidenceTier(0.70)).toBe('calibrated_statistical')
    expect(numericToConfidenceTier(0.40)).toBe('calibrated_statistical')
    expect(numericToConfidenceTier(0.15)).toBe('heuristic')
  })
})

describe('Task F4: Scoring Against Comparable Runs (Criterion 2)', () => {
  it('scores observed waste delta within predicted error bar as accurate', () => {
    const finding = createMockFinding({
      estimatedWasteTokens: 5000,
      errorBar: { lower: 4500, upper: 5500 },
      confidence: 'deterministic',
    })
    const prediction = recordPrediction(finding)

    // Observed savings of 4900 tokens (within [4500, 5500])
    const score = scorePrediction(prediction, {
      observedDeltaTokens: 4900,
      comparisonRunId: 'run-comparison-beta',
    })

    expect(score.predictionId).toBe(prediction.id)
    expect(score.predictedWasteTokens).toBe(5000)
    expect(score.observedDeltaTokens).toBe(4900)
    expect(score.absoluteError).toBe(100)
    expect(score.relativeError).toBeCloseTo(100 / 5000)
    expect(score.withinErrorBar).toBe(true)
    expect(score.isAccurate).toBe(true)
    expect(score.comparisonRunId).toBe('run-comparison-beta')

    // Brier score for p=0.95, outcome y=1: (0.95 - 1.0)^2 = 0.0025
    expect(score.brierScore).toBeCloseTo(0.0025)
  })

  it('scores observed waste delta outside error bar as inaccurate with higher Brier penalty', () => {
    const finding = createMockFinding({
      estimatedWasteTokens: 5000,
      errorBar: { lower: 4500, upper: 5500 },
      confidence: 'deterministic',
    })
    const prediction = recordPrediction(finding)

    // Observed savings of only 2000 tokens (well outside [4500, 5500])
    const score = scorePrediction(prediction, {
      observedDeltaTokens: 2000,
      comparisonRunId: 'run-comparison-beta',
    })

    expect(score.withinErrorBar).toBe(false)
    expect(score.isAccurate).toBe(false)
    expect(score.absoluteError).toBe(3000)
    expect(score.relativeError).toBeCloseTo(0.60)

    // Brier score for p=0.95, outcome y=0: (0.95 - 0)^2 = 0.9025
    expect(score.brierScore).toBeCloseTo(0.9025)
  })

  it('scores prediction against comparable run pair from G4 candidate pairing', () => {
    const runA = createMockCandidateRun({
      runId: 'run-baseline',
      taskFamily: 'refactor-auth',
      workingDirectory: '/work/app',
      repo: 'org/repo',
      started: '2026-09-06T10:00:00.000Z',
    })

    const runB = createMockCandidateRun({
      runId: 'run-optimized',
      taskFamily: 'refactor-auth',
      workingDirectory: '/work/app',
      repo: 'org/repo',
      started: '2026-09-06T11:00:00.000Z',
    })

    const finding = createMockFinding({
      runId: 'run-baseline',
      estimatedWasteTokens: 3000,
      errorBar: { lower: 2500, upper: 3500 },
    })
    const prediction = recordPrediction(finding)

    const score = scorePredictionAgainstPair(prediction, runA, runB, {
      observedDeltaTokens: 3100,
    })

    expect(score.comparisonRunId).toBe('run-optimized')
    expect(score.isAccurate).toBe(true)
    expect(score.observedDeltaTokens).toBe(3100)
  })

  it('refuses to score against non-comparable run pair', () => {
    const runA = createMockCandidateRun({
      runId: 'run-a',
      taskFamily: 'refactor-auth',
      workingDirectory: '/work/app',
      repo: 'org/repo-a',
      started: '2026-09-01T00:00:00.000Z',
    })

    const runB = createMockCandidateRun({
      runId: 'run-b',
      taskFamily: 'build-docs',
      workingDirectory: '/other/path',
      repo: 'org/repo-b',
      started: '2026-09-06T00:00:00.000Z',
    })

    const prediction = recordPrediction(createMockFinding({ runId: 'run-a' }))

    expect(() =>
      scorePredictionAgainstPair(prediction, runA, runB, { minConfidence: 0.5 })
    ).toThrow(/Pairing refusal/)
  })
})

describe('Task F4: Calibration Curve and Binning (Criterion 3, 4, 5)', () => {
  it('groups predictions into 5 confidence bins and computes accuracy and error per bin', () => {
    // 5 synthetic scores distributed across bins
    const scores: CalibrationScore[] = [
      // Bin 0.0-0.2: confidence 0.15, accurate = false
      {
        predictionId: 'p1',
        findingId: 'f1',
        runId: 'r1',
        comparisonRunId: 'r2',
        predictedWasteTokens: 500,
        observedDeltaTokens: 100,
        absoluteError: 400,
        relativeError: 0.8,
        errorBar: { lower: 400, upper: 600 },
        withinErrorBar: false,
        isAccurate: false,
        confidence: 0.15,
        brierScore: 0.0225,
        scoredAt: '2026-09-06T12:00:00Z',
      },
      // Bin 0.2-0.4: confidence 0.30, accurate = false
      {
        predictionId: 'p2',
        findingId: 'f2',
        runId: 'r1',
        comparisonRunId: 'r2',
        predictedWasteTokens: 800,
        observedDeltaTokens: 200,
        absoluteError: 600,
        relativeError: 0.75,
        errorBar: { lower: 600, upper: 1000 },
        withinErrorBar: false,
        isAccurate: false,
        confidence: 0.30,
        brierScore: 0.09,
        scoredAt: '2026-09-06T12:00:00Z',
      },
      // Bin 0.4-0.6: confidence 0.50, accurate = true
      {
        predictionId: 'p3',
        findingId: 'f3',
        runId: 'r1',
        comparisonRunId: 'r2',
        predictedWasteTokens: 1500,
        observedDeltaTokens: 1450,
        absoluteError: 50,
        relativeError: 0.033,
        errorBar: { lower: 1200, upper: 1800 },
        withinErrorBar: true,
        isAccurate: true,
        confidence: 0.50,
        brierScore: 0.25,
        scoredAt: '2026-09-06T12:00:00Z',
      },
      // Bin 0.6-0.8: confidence 0.70, accurate = true
      {
        predictionId: 'p4',
        findingId: 'f4',
        runId: 'r1',
        comparisonRunId: 'r2',
        predictedWasteTokens: 2000,
        observedDeltaTokens: 1950,
        absoluteError: 50,
        relativeError: 0.025,
        errorBar: { lower: 1600, upper: 2400 },
        withinErrorBar: true,
        isAccurate: true,
        confidence: 0.70,
        brierScore: 0.09,
        scoredAt: '2026-09-06T12:00:00Z',
      },
      // Bin 0.8-1.0: confidence 0.95, accurate = true
      {
        predictionId: 'p5',
        findingId: 'f5',
        runId: 'r1',
        comparisonRunId: 'r2',
        predictedWasteTokens: 3000,
        observedDeltaTokens: 3050,
        absoluteError: 50,
        relativeError: 0.016,
        errorBar: { lower: 2500, upper: 3500 },
        withinErrorBar: true,
        isAccurate: true,
        confidence: 0.95,
        brierScore: 0.0025,
        scoredAt: '2026-09-06T12:00:00Z',
      },
    ]

    const curve = calculateCalibrationCurve(scores)

    expect(curve.bins).toHaveLength(5)
    expect(curve.bins.map((b) => b.bin)).toEqual(CALIBRATION_BINS)

    // Bin 0.0-0.2: 1 prediction, observedAccuracy 0.0, meanConfidence 0.15, calError 0.15
    const b0 = curve.bins[0]
    expect(b0.predictionCount).toBe(1)
    expect(b0.observedAccuracy).toBe(0.0)
    expect(b0.meanConfidence).toBe(0.15)
    expect(b0.calibrationError).toBe(0.15)

    // Bin 0.8-1.0: 1 prediction, observedAccuracy 1.0, meanConfidence 0.95, calError 0.05
    const b4 = curve.bins[4]
    expect(b4.predictionCount).toBe(1)
    expect(b4.observedAccuracy).toBe(1.0)
    expect(b4.meanConfidence).toBe(0.95)
    expect(b4.calibrationError).toBe(0.05)

    expect(curve.totalPredictions).toBe(5)
    expect(curve.scoredPredictions).toBe(5)
    expect(curve.meanCalibrationError).toBeGreaterThan(0)
    expect(curve.expectedCalibrationError).toBeGreaterThan(0)
    expect(curve.isCalibrated).toBe(true)
    expect(curve.status).toBe('calibrated')
  })

  it('reports "not_yet_calibrated" when scored predictions count < 5 (Criterion 4)', () => {
    // Only 3 scored predictions (threshold is 5)
    const scores: CalibrationScore[] = [
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 1000 })), 1050),
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 2000 })), 1950),
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 3000 })), 3000),
    ]

    const curve = calculateCalibrationCurve(scores)

    expect(curve.scoredPredictions).toBe(3)
    expect(curve.isCalibrated).toBe(false)
    expect(curve.status).toBe('not_yet_calibrated')
    expect(curve.statusMessage).toContain('requires at least 5 scored predictions')
    expect(curve.demoteConfidence).toBe(true)
  })

  it('demotes confidence to named tiers when calibration error exceeds threshold (0.20)', () => {
    // 5 predictions where high confidence predictions all failed (high calibration error)
    const poorlyCalibratedScores: CalibrationScore[] = [
      // Bin 0.8-1.0 predictions, but none were accurate
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 5000, confidence: 'deterministic' })), 500),
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 5000, confidence: 'deterministic' })), 600),
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 5000, confidence: 'deterministic' })), 700),
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 5000, confidence: 'deterministic' })), 800),
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 5000, confidence: 'deterministic' })), 900),
    ]

    const curve = calculateCalibrationCurve(poorlyCalibratedScores)

    expect(curve.isCalibrated).toBe(true)
    expect(curve.meanCalibrationError).toBeGreaterThan(CALIBRATION_ERROR_THRESHOLD)
    expect(curve.demoteConfidence).toBe(true)

    // Verify formatConfidenceDisplay demotes to named tier
    const display = formatConfidenceDisplay(0.95, curve)
    expect(display.isDemoted).toBe(true)
    expect(display.tier).toBe('deterministic')
    expect(display.display).toBe('deterministic') // demoted to named tier rather than '95%'
  })

  it('formats numeric percentage when well-calibrated and within threshold', () => {
    // 5 predictions with accurate outcomes matching high confidence
    const wellCalibratedScores: CalibrationScore[] = [
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 5000, confidence: 'deterministic' })), 5000),
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 4000, confidence: 'deterministic' })), 4050),
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 3000, confidence: 'deterministic' })), 2950),
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 2000, confidence: 'deterministic' })), 2020),
      scorePrediction(recordPrediction(createMockFinding({ estimatedWasteTokens: 1000, confidence: 'deterministic' })), 990),
    ]

    const curve = calculateCalibrationCurve(wellCalibratedScores)

    expect(curve.isCalibrated).toBe(true)
    expect(curve.meanCalibrationError).toBeLessThanOrEqual(CALIBRATION_ERROR_THRESHOLD)
    expect(curve.demoteConfidence).toBe(false)

    const display = formatConfidenceDisplay(0.95, curve)
    expect(display.isDemoted).toBe(false)
    expect(display.display).toBe('95%')
  })
})

describe('Task F4: Synthetic Paired Run Outcomes', () => {
  it('simulates a cohort of paired runs, scores predictions, and builds calibration history', () => {
    const store = new CanonStore(':memory:')

    const taskFamily = 'performance-optimization'
    const pairedRuns = [
      { baselineRun: 'run-b1', treatedRun: 'run-t1', predictedWaste: 4000, observedSavings: 3900 },
      { baselineRun: 'run-b2', treatedRun: 'run-t2', predictedWaste: 2500, observedSavings: 2400 },
      { baselineRun: 'run-b3', treatedRun: 'run-t3', predictedWaste: 6000, observedSavings: 6100 },
      { baselineRun: 'run-b4', treatedRun: 'run-t4', predictedWaste: 1500, observedSavings: 1450 },
      { baselineRun: 'run-b5', treatedRun: 'run-t5', predictedWaste: 3200, observedSavings: 3100 },
      { baselineRun: 'run-b6', treatedRun: 'run-t6', predictedWaste: 8000, observedSavings: 8200 },
    ]

    for (const pair of pairedRuns) {
      const finding = createMockFinding({
        id: `f-${pair.baselineRun}`,
        runId: pair.baselineRun,
        estimatedWasteTokens: pair.predictedWaste,
        confidence: 'deterministic',
        errorBar: {
          lower: Math.round(pair.predictedWaste * 0.85),
          upper: Math.round(pair.predictedWaste * 1.15),
        },
      })

      // 1. Log prediction at render time
      const prediction = recordPrediction(finding)
      store.upsertPrediction(prediction)

      // 2. Score against paired run outcome
      const score = scorePrediction(prediction, {
        observedDeltaTokens: pair.observedSavings,
        comparisonRunId: pair.treatedRun,
      })

      // 3. Update prediction with score
      store.upsertPrediction({
        ...prediction,
        comparisonRunId: pair.treatedRun,
        observedDeltaTokens: pair.observedSavings,
        calibrationScore: score.brierScore,
      })
    }

    expect(store.predictionCount()).toBe(6)

    const scored = store.listPredictions({ scoredOnly: true })
    expect(scored).toHaveLength(6)

    const summary = store.getCalibrationSummary()
    expect(summary.isCalibrated).toBe(true)
    expect(summary.status).toBe('calibrated')
    expect(summary.scoredPredictions).toBe(6)
    expect(summary.meanCalibrationError).toBeLessThan(0.10)
    expect(summary.demoteConfidence).toBe(false)

    store.close()
  })
})

describe('Task F4: CanonStore Persistence & Migration 8 -> v9 (Criterion 4)', () => {
  it('stamps the current SCHEMA_VERSION on fresh stores and creates prediction table and indexes', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)

    // The stamp tracks SCHEMA_VERSION rather than a literal: what this test is
    // about is the prediction table, and pinning the number here meant a later
    // schema bump failed a calibration test that had nothing to do with it.
    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    // v9 is the version that introduced `prediction`; every later one keeps it.
    expect(SCHEMA_VERSION).toBeGreaterThanOrEqual(9)
    expect(store.predictionCount()).toBe(0)

    // Inspect SQLite master for table and indexes
    const db = new DatabaseSync(path)
    const tables = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='prediction'").all()
    expect(tables).toHaveLength(1)

    const indexes = db
      .prepare("SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='prediction'")
      .all() as { name: string }[]
    const indexNames = indexes.map((i) => i.name)
    expect(indexNames).toContain('prediction_by_finding')
    expect(indexNames).toContain('prediction_by_run')
    expect(indexNames).toContain('prediction_by_created_at')

    db.close()
    store.close()
  })

  it('migrates a v8 store forward, preserving data and adding prediction table with indexes', () => {
    const path = tempStorePath()
    const initStore = new CanonStore(path)
    initStore.close()

    // Simulate existing v8 store: downgrade schema_version to 8 and drop prediction table
    const db = new DatabaseSync(path)
    db.prepare("UPDATE metadata SET value = '8' WHERE key = 'schema_version'").run()
    db.exec('DROP TABLE IF EXISTS prediction')
    db.close()

    // Reopen store to trigger migration from 8 up to the current version
    const store = new CanonStore(path)
    expect(store.getMetadata('schema_version')).toBe(String(SCHEMA_VERSION))
    expect(store.predictionCount()).toBe(0)

    // Verify prediction table was created by migration
    const prediction: PredictionRecord = {
      id: 'pred-migrated-1',
      findingId: 'f-migrated-1',
      runId: 'run-migrated',
      predictedWasteTokens: 2500,
      confidence: 0.95,
      confidenceTier: 'deterministic',
      errorBar: { lower: 2000, upper: 3000 },
      createdAt: '2026-09-06T16:00:00.000Z',
      status: 'pending',
    }
    store.upsertPrediction(prediction)

    expect(store.predictionCount()).toBe(1)
    const retrieved = store.getPrediction('pred-migrated-1')
    expect(retrieved?.findingId).toBe('f-migrated-1')
    expect(retrieved?.predictedWasteTokens).toBe(2500)

    store.close()
  })

  it('supports single and batch upserts, queries by runId, findingId, and scoredOnly', () => {
    const store = new CanonStore(':memory:')

    const pred1: PredictionRecord = {
      id: 'p-1',
      findingId: 'f-1',
      runId: 'run-a',
      predictedWasteTokens: 1000,
      confidence: 0.95,
      createdAt: '2026-09-06T12:00:00.000Z',
    }

    const pred2: PredictionRecord = {
      id: 'p-2',
      findingId: 'f-2',
      runId: 'run-a',
      predictedWasteTokens: 2000,
      confidence: 0.70,
      createdAt: '2026-09-06T12:01:00.000Z',
      comparisonRunId: 'run-b',
      observedDeltaTokens: 2100,
      calibrationScore: 0.05,
    }

    const pred3: PredictionRecord = {
      id: 'p-3',
      findingId: 'f-3',
      runId: 'run-c',
      predictedWasteTokens: 3000,
      confidence: 0.15,
      createdAt: '2026-09-06T12:02:00.000Z',
    }

    store.upsertPredictions([pred1, pred2, pred3])
    expect(store.predictionCount()).toBe(3)

    // Query by runId
    const runAPredictions = store.listPredictions({ runId: 'run-a' })
    expect(runAPredictions).toHaveLength(2)

    // Query by findingId
    const f2Predictions = store.listPredictions({ findingId: 'f-2' })
    expect(f2Predictions).toHaveLength(1)
    expect(f2Predictions[0].id).toBe('p-2')

    // Query scoredOnly
    const scoredPredictions = store.listPredictions({ scoredOnly: true })
    expect(scoredPredictions).toHaveLength(1)
    expect(scoredPredictions[0].id).toBe('p-2')

    // Query with limit
    const limited = store.listPredictions({ limit: 2 })
    expect(limited).toHaveLength(2)

    // Delete one prediction
    store.deletePrediction('p-1')
    expect(store.predictionCount()).toBe(2)
    expect(store.getPrediction('p-1')).toBeUndefined()

    store.close()
  })
})

describe('Task F4: HTTP Endpoints and Bridge Integration', () => {
  it('serves predictions via GET and logs via POST on /api/kyber/predictions', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)
    const bridge = new KyberBridge({ canonPath: path, store })

    // 1. Initial GET should return empty list
    let statusCode = 0
    let responseBody = ''
    const reqGet = { method: 'GET' } as any
    const resGet = {
      writeHead: (status: number) => {
        statusCode = status
      },
      end: (data: string) => {
        responseBody = data
      },
    } as any

    let handled = handleKyberRequest(
      reqGet,
      resGet,
      new URL('http://localhost:3000/api/kyber/predictions'),
      bridge
    )
    expect(handled).toBe(true)
    expect(statusCode).toBe(200)
    let parsed = JSON.parse(responseBody)
    expect(parsed.predictions).toEqual([])

    // 2. POST to record a prediction
    let postStatusCode = 0
    let postResponseBody = ''
    const postPayload = JSON.stringify({
      findingId: 'f-post-1',
      runId: 'run-post',
      predictedWasteTokens: 4200,
      confidence: 'deterministic',
      errorBar: { lower: 3800, upper: 4600 },
    })

    const reqPost = {
      method: 'POST',
      on: (event: string, callback: (chunk?: any) => void) => {
        if (event === 'data') callback(postPayload)
        if (event === 'end') callback()
      },
    } as any

    const resPost = {
      writeHead: (status: number) => {
        postStatusCode = status
      },
      end: (data: string) => {
        postResponseBody = data
      },
    } as any

    handled = handleKyberRequest(
      reqPost,
      resPost,
      new URL('http://localhost:3000/api/kyber/predictions'),
      bridge
    )
    expect(handled).toBe(true)
    expect(postStatusCode).toBe(201)
    const postResult = JSON.parse(postResponseBody)
    expect(postResult.prediction.findingId).toBe('f-post-1')
    expect(postResult.prediction.predictedWasteTokens).toBe(4200)

    // 3. GET should now return the logged prediction
    const resGet2 = {
      writeHead: (status: number) => {
        statusCode = status
      },
      end: (data: string) => {
        responseBody = data
      },
    } as any
    handleKyberRequest(
      reqGet,
      resGet2,
      new URL('http://localhost:3000/api/kyber/predictions?runId=run-post'),
      bridge
    )
    expect(statusCode).toBe(200)
    parsed = JSON.parse(responseBody)
    expect(parsed.predictions).toHaveLength(1)
    expect(parsed.predictions[0].findingId).toBe('f-post-1')

    bridge.close()
    store.close()
  })

  it('serves calibration curve on /api/kyber/calibration', () => {
    const path = tempStorePath()
    const store = new CanonStore(path)
    const bridge = new KyberBridge({ canonPath: path, store })

    // Seed 5 scored predictions into the store
    for (let i = 1; i <= 5; i++) {
      store.upsertPrediction({
        id: `p-${i}`,
        findingId: `f-${i}`,
        runId: 'run-calibration-api',
        predictedWasteTokens: 1000 * i,
        confidence: 0.95,
        confidenceTier: 'deterministic',
        errorBar: { lower: 800 * i, upper: 1200 * i },
        createdAt: new Date().toISOString(),
        comparisonRunId: `run-comparison-${i}`,
        observedDeltaTokens: 1000 * i + 50,
        calibrationScore: 0.0025,
      })
    }

    let statusCode = 0
    let responseBody = ''
    const req = { method: 'GET' } as any
    const res = {
      writeHead: (status: number) => {
        statusCode = status
      },
      end: (data: string) => {
        responseBody = data
      },
    } as any

    const handled = handleKyberRequest(
      req,
      res,
      new URL('http://localhost:3000/api/kyber/calibration?runId=run-calibration-api'),
      bridge
    )

    expect(handled).toBe(true)
    expect(statusCode).toBe(200)
    const curve = JSON.parse(responseBody)
    expect(curve.isCalibrated).toBe(true)
    expect(curve.status).toBe('calibrated')
    expect(curve.scoredPredictions).toBe(5)
    expect(curve.bins).toHaveLength(5)

    bridge.close()
    store.close()
  })

  it('rejects unsupported HTTP methods with 405', () => {
    const bridge = new KyberBridge({ canonPath: ':memory:' })
    let statusCode = 0
    let responseBody = ''
    const res = {
      writeHead: (status: number) => {
        statusCode = status
      },
      end: (data: string) => {
        responseBody = data
      },
    } as any

    const reqDelete = { method: 'DELETE' } as any
    const handled = handleKyberRequest(
      reqDelete,
      res,
      new URL('http://localhost:3000/api/kyber/predictions'),
      bridge
    )
    expect(handled).toBe(true)
    expect(statusCode).toBe(405)
    expect(JSON.parse(responseBody).error).toBe('Method Not Allowed')

    bridge.close()
  })
})
