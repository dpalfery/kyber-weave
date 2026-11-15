// Boot an isolated C# kyberdash dashboard against the Factory replay temp DB. Prints FR_READY <url>.

import { bootIsolatedWorld } from './harness-collection-world.js'

await bootIsolatedWorld('factory-replay', 'FR')
