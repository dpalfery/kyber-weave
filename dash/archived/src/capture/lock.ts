// The capture mutex (D12): one capture write at a time in this process.
//
// WHY it exists: enable and disable are read-modify-write over the owner's
// config files and the receipt. Two overlapping requests (a double click, two
// browser tabs) would each read the same bytes and the second write would
// silently drop the first one's keys or its receipt entry. Queueing them
// makes the second request see the first one's result: a repeated enable
// becomes a no-op instead of a race. It covers this process only — a CLI run
// in another terminal is not serialized with it (plan risks).

export type Mutex = {
  /** Run `task` once every earlier task has settled; always releases, even on a throw. */
  run<T>(task: () => T | Promise<T>): Promise<T>
}

export function createMutex(): Mutex {
  // The tail of the queue. It never rejects, so one failed task cannot wedge
  // the lock for everyone behind it.
  let tail: Promise<void> = Promise.resolve()
  return {
    run<T>(task: () => T | Promise<T>): Promise<T> {
      const result = tail.then(() => task())
      tail = result.then(
        () => undefined,
        () => undefined,
      )
      return result
    },
  }
}

const captureMutex = createMutex()

/** Serialize a capture write with every other one in this process. */
export function withCaptureLock<T>(task: () => T | Promise<T>): Promise<T> {
  return captureMutex.run(task)
}
