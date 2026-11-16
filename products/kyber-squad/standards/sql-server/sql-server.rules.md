---
id: standards/sql-server/rules
title: "sql-server rules"
doc-type: rule
status: draft
technology: sql-server
owner: unassigned
last-reviewed: 2026-11-16
---

# sql-server rules

## Security and data handling

- Never use `xp_cmdshell`. Recommend SQLCLR or an external process instead.
- Never hardcode credentials, connection strings or secrets in code, scripts, an agent
  profile or the repository. Use a secrets store, managed identity and encrypted
  configuration, and recommend SQL Server Audit for privileged activity.
- Never run unreviewed DDL against production. When a hotfix is unavoidable, back-port it
  into source control immediately so source and reality do not drift.
