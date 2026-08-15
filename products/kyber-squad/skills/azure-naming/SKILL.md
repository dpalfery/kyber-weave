---
name: azure-naming
description: Use the Azure naming convention to name resources.
license: MIT
metadata:
  author: David R Palfery
  version: 2.0.0
---

# Azure Naming Standards

Follow the naming convention declared as **Azure Naming Standard** in the root `AGENTS.md` Repository Configuration & Paths registry exactly — general pattern, environment codes, region codes, resource-type slugs, and per-resource length/character constraints and truncation rules are all defined there. Do not restate, fork, or improvise a different convention here.

Before creating any Azure resource: construct the name from that document's pattern, validate it against that document's length/character constraints for the specific resource type, and use the CAF resource-abbreviations guide it links to for any resource type not already listed.
