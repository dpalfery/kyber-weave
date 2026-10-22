# Vendored CycloneDX schemas

These are the upstream JSON Schemas for CycloneDX 1.6, vendored so that SBOM validation
is hermetic: it runs offline, with no network fetch and no second external tool in the
release path. The only external binary the release pipeline runs is Syft, which
`scripts/generate-sbom.sh` downloads and verifies by SHA-256.

## Provenance

| | |
|---|---|
| Upstream repository | <https://github.com/CycloneDX/specification> |
| Upstream commit | `ad83dae28b236b8760286399da4daba77592de86` (branch `master`) |
| Upstream path | `schema/` |
| Retrieved | 2026-10-21 |

| File | SHA-256 |
|---|---|
| `bom-1.6.schema.json` | `18f57f7482593bad9f21b4feed09084640cbeff419d62ad5090c5ceccca5b37d` |
| `spdx.schema.json` | `4b345e2329f209f34e960ae2a8e7cb46a166907e6a45e94978565925dc47b359` |
| `jsf-0.82.schema.json` | `8bae002c25e723db7ee1f26afde680ae1a2b1a8f6b4b4b0fd65dc3becb090aae` |

All three are required: `bom-1.6.schema.json` references the other two by relative
filename (`spdx.schema.json` for the SPDX licence-id enumeration, and
`jsf-0.82.schema.json#/definitions/signature` for JSON Signature Format blocks). The
validator resolves those references by filename within this directory.

## Refreshing

Bump the upstream commit, re-download the three files, and update the hashes above. The
validator does not read these hashes at runtime; they are here so a reviewer can check
the vendored copies against upstream without trusting this repository's history:

```bash
commit=ad83dae28b236b8760286399da4daba77592de86
for f in bom-1.6.schema.json spdx.schema.json jsf-0.82.schema.json; do
  curl -fsSL -o "$f" \
    "https://raw.githubusercontent.com/CycloneDX/specification/${commit}/schema/${f}"
  shasum -a 256 "$f"
done
```

## What the validator implements

`scripts/validate-sbom.py` implements the subset of JSON Schema Draft 7 that these files
actually use: `type`, `required`, `properties`, `additionalProperties`, `items` (both the
single-schema and tuple forms), `additionalItems`, `enum`, `pattern`, `$ref`, `oneOf`,
`anyOf`, `allOf`, `if`/`then`/`else`, `uniqueItems`, `minimum`, `maximum`, `minItems`,
`maxLength` and `minLength`.

`format` is deliberately not asserted. In Draft 7 `format` is an annotation, and the
reference implementation (`jsonschema`) does not assert it either unless a format
checker is explicitly supplied — so asserting it here would make this validator
*stricter* than the reference, and the two would disagree on documents that the
reference accepts.

The keyword list above is not a guess. It was obtained by walking the three vendored
files and collecting every keyword that appears, and it is checked against the reference
implementation by a test in the repository's test suite.
