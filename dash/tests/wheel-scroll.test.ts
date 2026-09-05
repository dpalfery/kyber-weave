import { describe, it, expect } from 'vitest'

import { wheelDelta } from '../src/dashboard.js'

describe('wheelDelta', () => {
  it('scrolls down three lines per wheel-down tick (button 65)', () => {
    expect(wheelDelta('\x1b[<65;10;5M')).toBe(3)
  })

  it('scrolls up three lines per wheel-up tick (button 64)', () => {
    expect(wheelDelta('\x1b[<64;10;5M')).toBe(-3)
  })

  it('sums multiple ticks in one chunk, mixed directions', () => {
    expect(wheelDelta('\x1b[<65;1;1M\x1b[<65;1;1M\x1b[<64;1;1M')).toBe(3)
  })

  it('ignores clicks, releases, drags, and modified wheel events', () => {
    // press, release, drag, shift+wheel (button 69), ctrl+wheel (80)
    expect(wheelDelta('\x1b[<0;12;7M\x1b[<0;12;7m\x1b[<32;13;7M\x1b[<69;1;1M\x1b[<80;1;1M')).toBe(0)
  })

  it('ignores plain keyboard input and partial sequences', () => {
    expect(wheelDelta('q')).toBe(0)
    expect(wheelDelta('\x1b[B')).toBe(0)
    expect(wheelDelta('\x1b[<65;10')).toBe(0)
  })
})
