---
id: todo/kyberdash-test-port-3000-econnrefused
title: Explain the ECONNREFUSED 127.0.0.1:3000 lines seen in the dash suite log
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-30
---

# Explain the ECONNREFUSED 127.0.0.1:3000 lines seen in the dash suite log

The dash test log for the storm guard closeout (2026-10-30) shows `ECONNREFUSED 127.0.0.1:3000`
lines. Something in the dash suite connects to a local port 3000 and finds nothing listening. The
connection is refused, so the suite does not hang, and it may be a deliberate probe for a
server that is not running.

Investigate:

1. Find the test or helper that opens a connection to `127.0.0.1:3000` or `localhost:3000`, and
   whether it expects the refusal.
2. Confirm that the probe starts no process and does not reach a developer's own server on that
   port. If it does reach one, it is a test-isolation defect, and the fix belongs with the test.
3. If the refusal is expected, say so in that test and drop the noise. If it is not, fix the probe.

This todo is complete when the source of the lines is named and either explained in the test or
fixed.
