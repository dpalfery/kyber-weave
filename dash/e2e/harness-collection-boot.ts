// Boot an isolated C# kyberdash dashboard against the harness-collection temp DB. Prints HC_READY <url>.

import { bootIsolatedWorld } from './harness-collection-world.js'

await bootIsolatedWorld('harness-collection', 'HC')
