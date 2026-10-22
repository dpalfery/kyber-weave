#!/usr/bin/env python3
"""Validate a CycloneDX JSON SBOM against the vendored CycloneDX 1.6 schema.

Why this exists rather than a `dotnet tool` or the CycloneDX CLI: the release
pipeline already downloads one external binary (Syft) and verifies it by SHA-256.
Adding a second one means a second supply-chain decision, and `cyclonedx-cli` does
not publish a checksum manifest with its releases, so its hash would have to be
taken on trust from this repository. The schema is instead vendored under
`scripts/schemas/` — pinned to an upstream commit, with the hashes recorded in
`scripts/schemas/PROVENANCE.md` — so validation is hermetic and offline.

What this implements is the subset of JSON Schema Draft 7 that the CycloneDX 1.6
schema actually uses. See `scripts/schemas/PROVENANCE.md` for the keyword list and
for why `format` is not asserted.

Usage:
    scripts/validate-sbom.py <file> [--schema-dir DIR]
                              [--require-ecosystems npm,cargo,nuget]
                              [--optional-ecosystems nuget]

`--require-ecosystems` names the ecosystems that must be present. An ecosystem
also named in `--optional-ecosystems` is reported as a warning when it is
missing rather than failing the run: that is how the nuget coverage is
handled when there is no .NET SDK and the SBOM is knowingly partial.

Exit codes:
    0  the SBOM is valid
    1  the SBOM is invalid (schema violation, empty component list, missing
       ecosystem, or an unresolvable dependency graph)
    2  the validator could not run (missing file, missing schema, bad arguments)
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from typing import Any

DEFAULT_SCHEMA_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "schemas")
SCHEMA_FILE = "bom-1.6.schema.json"

# The ecosystems a Kyber-Weave release ships. The npm and cargo checks are
# unconditional: Syft parses those lock files directly, so their absence means the
# scan did not do what it was asked to do. The nuget check is unconditional too
# when the .NET SDK staged a publish, and a warning when it did not — without the
# SDK the SBOM is knowingly partial, and the caller says so.
ECOSYSTEM_PREFIXES = {
    "npm": "pkg:npm",
    "cargo": "pkg:cargo",
    "nuget": "pkg:nuget",
}


class SchemaError(Exception):
    """The schema itself could not be loaded or a reference could not be resolved."""


class SchemaStore:
    """Loads schema documents and resolves `$ref` across them."""

    def __init__(self, schema_dir: str) -> None:
        self.schema_dir = schema_dir
        self._documents: dict[str, Any] = {}

    def document(self, name: str) -> Any:
        if name not in self._documents:
            path = os.path.join(self.schema_dir, name)
            if not os.path.isfile(path):
                raise SchemaError(f"schema file not found: {path}")
            try:
                with open(path, encoding="utf-8") as handle:
                    self._documents[name] = json.load(handle)
            except json.JSONDecodeError as error:
                raise SchemaError(f"{path} is not valid JSON: {error}") from error
        return self._documents[name]

    def resolve(self, ref: str, base: str) -> tuple[Any, str]:
        """Resolve a `$ref` to (schema, base-document-name).

        A local reference (`#/definitions/x`) stays in the current document. A
        reference to another file (`spdx.schema.json`, or
        `jsf-0.82.schema.json#/definitions/signature`) loads that file and, if it
        carries a JSON pointer, descends into it.
        """
        if ref.startswith("#"):
            document = self.document(base)
            pointer = ref[1:]
            if not pointer:
                return document, base
            target = _pointer(document, pointer)
            if target is None:
                raise SchemaError(f"cannot resolve {ref!r} in {base}")
            return target, base

        file_part, _, pointer = ref.partition("#")
        document = self.document(file_part)
        if not pointer:
            return document, file_part
        target = _pointer(document, pointer)
        if target is None:
            raise SchemaError(f"cannot resolve {ref!r} in {file_part}")
        return target, file_part


def _pointer(document: Any, pointer: str) -> Any:
    """Resolve a JSON pointer (`/definitions/foo`) inside a document."""
    if pointer in ("", "/"):
        return document
    node = document
    for raw in pointer.lstrip("/").split("/"):
        token = raw.replace("~1", "/").replace("~0", "~")
        if isinstance(node, dict):
            if token not in node:
                return None
            node = node[token]
        elif isinstance(node, list):
            try:
                node = node[int(token)]
            except (ValueError, IndexError):
                return None
        else:
            return None
    return node


def _canonical(value: Any) -> str:
    """A stable text form of a JSON value, for uniqueItems comparison."""
    return json.dumps(value, sort_keys=True, separators=(",", ":"), default=str)


def _type_matches(instance: Any, expected: str) -> bool:
    if expected == "object":
        return isinstance(instance, dict)
    if expected == "array":
        return isinstance(instance, list)
    if expected == "string":
        return isinstance(instance, str)
    if expected == "boolean":
        return isinstance(instance, bool)
    if expected == "null":
        return instance is None
    if expected == "number":
        return isinstance(instance, (int, float)) and not isinstance(instance, bool)
    if expected == "integer":
        return isinstance(instance, int) and not isinstance(instance, bool)
    return True


class Validator:
    """A focused JSON Schema Draft 7 validator.

    Only the keywords the CycloneDX 1.6 schema uses are implemented. Anything else
    in a schema is ignored rather than silently treated as satisfied, which is why
    the keyword list is recorded in PROVENANCE.md and covered by a test.
    """

    def __init__(self, store: SchemaStore) -> None:
        self.store = store

    def validate(self, instance: Any, schema: Any, base: str) -> list[str]:
        if schema is True:
            return []
        if schema is False:
            return ["value is not allowed here"]
        if not isinstance(schema, dict):
            return []

        # Draft 7: `$ref` replaces the schema it appears in; sibling keywords are
        # annotations and are ignored.
        if "$ref" in schema:
            target, target_base = self.store.resolve(schema["$ref"], base)
            return self.validate(instance, target, target_base)

        errors: list[str] = []

        expected_type = schema.get("type")
        if expected_type is not None:
            types = expected_type if isinstance(expected_type, list) else [expected_type]
            if not any(_type_matches(instance, item) for item in types):
                errors.append(
                    "expected type %s, got %s" % ("/".join(types), _type_name(instance))
                )
                # A wrong type makes every other keyword's expectation meaningless
                # for this value, so stop here rather than emit noise.
                return errors

        if "enum" in schema and instance not in schema["enum"]:
            errors.append("value is not one of the permitted values")

        if isinstance(instance, str):
            pattern = schema.get("pattern")
            if pattern is not None and re.search(pattern, instance) is None:
                errors.append(f"string does not match pattern {pattern!r}")
            if "minLength" in schema and len(instance) < schema["minLength"]:
                errors.append(f"string is shorter than {schema['minLength']}")
            if "maxLength" in schema and len(instance) > schema["maxLength"]:
                errors.append(f"string is longer than {schema['maxLength']}")

        if isinstance(instance, (int, float)) and not isinstance(instance, bool):
            if "minimum" in schema and instance < schema["minimum"]:
                errors.append(f"value is less than the minimum {schema['minimum']}")
            if "maximum" in schema and instance > schema["maximum"]:
                errors.append(f"value is greater than the maximum {schema['maximum']}")

        if isinstance(instance, dict):
            errors.extend(self._validate_object(instance, schema, base))
        elif isinstance(instance, list):
            errors.extend(self._validate_array(instance, schema, base))

        if "allOf" in schema:
            for subschema in schema["allOf"]:
                errors.extend(self.validate(instance, subschema, base))

        if "oneOf" in schema:
            passing = [
                index
                for index, subschema in enumerate(schema["oneOf"])
                if not self.validate(instance, subschema, base)
            ]
            if len(passing) != 1:
                errors.append(
                    "value must match exactly one of the %d permitted forms (matched %d)"
                    % (len(schema["oneOf"]), len(passing))
                )

        if "anyOf" in schema:
            passing = [
                subschema
                for subschema in schema["anyOf"]
                if not self.validate(instance, subschema, base)
            ]
            if not passing:
                errors.append("value does not match any of the permitted forms")

        # `if` / `then` / `else`: `then` applies when `if` passes, `else` when it
        # does not. Only the CycloneDX signature schema uses this.
        if "if" in schema:
            condition_holds = not self.validate(instance, schema["if"], base)
            branch = "then" if condition_holds else "else"
            if branch in schema:
                errors.extend(self.validate(instance, schema[branch], base))

        return errors

    def _validate_object(self, instance: dict, schema: dict, base: str) -> list[str]:
        errors: list[str] = []
        properties = schema.get("properties", {}) or {}

        for name in schema.get("required", []) or []:
            if name not in instance:
                errors.append(f"missing required property {name!r}")

        additional = schema.get("additionalProperties", True)
        for name, value in instance.items():
            if name in properties:
                for message in self.validate(value, properties[name], base):
                    errors.append(f"property {name!r}: {message}")
            elif additional is False:
                errors.append(f"property {name!r} is not permitted here")
            elif isinstance(additional, dict):
                for message in self.validate(value, additional, base):
                    errors.append(f"property {name!r}: {message}")

        return errors

    def _validate_array(self, instance: list, schema: dict, base: str) -> list[str]:
        errors: list[str] = []
        item_schema = schema.get("items")

        if isinstance(item_schema, list):
            # Tuple form: each position has its own schema, and `additionalItems`
            # decides what happens past the last one. The CycloneDX licence-choice
            # definition uses this to say "exactly one SPDX expression".
            for index, item in enumerate(instance):
                if index < len(item_schema):
                    subschema = item_schema[index]
                else:
                    additional = schema.get("additionalItems", True)
                    if additional is False:
                        errors.append(f"item {index}: additional items are not permitted here")
                        continue
                    if not isinstance(additional, dict):
                        continue
                    subschema = additional
                for message in self.validate(item, subschema, base):
                    errors.append(f"item {index}: {message}")
        elif item_schema is not None:
            for index, item in enumerate(instance):
                for message in self.validate(item, item_schema, base):
                    errors.append(f"item {index}: {message}")

        if "minItems" in schema and len(instance) < schema["minItems"]:
            errors.append(f"array has fewer than {schema['minItems']} items")
        if "maxItems" in schema and len(instance) > schema["maxItems"]:
            errors.append(f"array has more than {schema['maxItems']} items")

        if schema.get("uniqueItems") is True:
            seen: set[str] = set()
            for item in instance:
                key = _canonical(item)
                if key in seen:
                    errors.append("array contains duplicate items")
                    break
                seen.add(key)

        return errors


def _type_name(value: Any) -> str:
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "boolean"
    if isinstance(value, int):
        return "integer"
    if isinstance(value, float):
        return "number"
    if isinstance(value, str):
        return "string"
    if isinstance(value, list):
        return "array"
    if isinstance(value, dict):
        return "object"
    return type(value).__name__


def validate_ecosystems(
    document: dict, required: list[str], optional: list[str]
) -> tuple[list[str], list[str]]:
    """Report ecosystems that have no components. Returns (errors, warnings).

    An ecosystem in `required` that is missing is an error. An ecosystem in
    `optional` that is missing is a warning: it is one the caller knows is only
    partly covered, and saying so is more useful than failing.
    """
    components = document.get("components") or []
    present: dict[str, int] = {}
    for component in components:
        purl = component.get("purl") or ""
        for name, prefix in ECOSYSTEM_PREFIXES.items():
            if purl.startswith(prefix + "/"):
                present[name] = present.get(name, 0) + 1

    errors: list[str] = []
    warnings: list[str] = []
    for name in required:
        count = present.get(name, 0)
        if count == 0:
            message = f"no {name} components found in the SBOM"
            if name in optional:
                warnings.append(message)
            else:
                errors.append(message)
    return errors, warnings


def validate_dependency_graph(document: dict) -> list[str]:
    """Every dependency reference must name a component that is in the document."""
    components = document.get("components") or []
    known = {component.get("bom-ref") for component in components}
    root = (document.get("metadata") or {}).get("component") or {}
    if root.get("bom-ref"):
        known.add(root["bom-ref"])

    errors: list[str] = []
    for entry in document.get("dependencies") or []:
        ref = entry.get("ref")
        if ref not in known:
            errors.append(f"dependency entry references an unknown component: {ref!r}")
        for target in entry.get("dependsOn") or []:
            if target not in known:
                errors.append(f"dependency entry {ref!r} depends on an unknown component: {target!r}")
    return errors


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("file", help="the CycloneDX JSON SBOM to validate")
    parser.add_argument(
        "--schema-dir",
        default=DEFAULT_SCHEMA_DIR,
        help="directory holding the vendored schemas (default: %(default)s)",
    )
    parser.add_argument(
        "--require-ecosystems",
        default="",
        help="comma-separated ecosystems that must have at least one component",
    )
    parser.add_argument(
        "--optional-ecosystems",
        default="",
        help="comma-separated ecosystems whose absence is a warning rather than an error",
    )
    arguments = parser.parse_args(argv)

    if not os.path.isfile(arguments.file):
        print(f"::error::SBOM not found: {arguments.file}", file=sys.stderr)
        return 2

    try:
        with open(arguments.file, encoding="utf-8") as handle:
            document = json.load(handle)
    except json.JSONDecodeError as error:
        print(f"::error::{arguments.file} is not valid JSON: {error}", file=sys.stderr)
        return 1

    if not isinstance(document, dict):
        print(f"::error::{arguments.file} is not a JSON object", file=sys.stderr)
        return 1

    try:
        store = SchemaStore(arguments.schema_dir)
        schema = store.document(SCHEMA_FILE)
    except SchemaError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 2

    validator = Validator(store)
    try:
        errors = validator.validate(document, schema, SCHEMA_FILE)
    except SchemaError as error:
        print(f"::error::{error}", file=sys.stderr)
        return 2

    components = document.get("components")
    if not isinstance(components, list) or not components:
        errors.append("the SBOM has no components")

    errors.extend(validate_dependency_graph(document))

    required = [name.strip() for name in arguments.require_ecosystems.split(",") if name.strip()]
    optional = [name.strip() for name in arguments.optional_ecosystems.split(",") if name.strip()]
    ecosystem_errors, ecosystem_warnings = validate_ecosystems(document, required, optional)
    errors.extend(ecosystem_errors)

    for message in ecosystem_warnings:
        print(f"::warning::{message}", file=sys.stderr)

    if errors:
        for message in errors:
            print(f"::error::{message}", file=sys.stderr)
        print(
            f"::error::{arguments.file} is not a valid CycloneDX SBOM "
            f"({len(errors)} problem(s))",
            file=sys.stderr,
        )
        return 1

    print(
        f"SBOM is valid: {arguments.file} "
        f"(CycloneDX {document.get('specVersion')}, {len(components)} components)"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
