/// Release public keys for ed25519 plugin signature verification.
/// Maps keyId (8 hex chars, derived from sha256 of PEM) to base64-encoded PEM public key.

export const RELEASE_PUBLIC_KEYS: ReadonlyMap<string, string> = new Map([
  ['499923ae', 'LS0tLS1CRUdJTiBQVUJMSUMgS0VZLS0tLS0KTUNvd0JRWURLMlZ3QXlFQTU1QTAvMUpLTlBoMGFsL2xMN2xhWFBobWVnZVhzUENqK3RoM1B4LzNWM0U9Ci0tLS0tRU5EIFBVQkxJQyBLRVktLS0tLQo='],
])
