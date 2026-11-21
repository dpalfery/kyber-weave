---
id: standards/sqlite/rules
title: "sqlite rules"
doc-type: rule
status: current
technology: sqlite
owner: dpalfery
last-reviewed: 2026-11-20
---

# sqlite rules

## The engine

- SQL in this repository is SQLite, reached through the system `sqlite3` CLI.
  `Microsoft.Data.Sqlite` and `SQLitePCLRaw` are not added.
- `canon.db` is the KyberDash product database. The CodeGraph index and the docs
  analysis cache are not that database and are not a source of its history.

## Schema changes

- A change to the canon schema edits `RelationalSchemaCatalog` and bumps
  `SqliteRelationalStore.SchemaVersion` in the same change.
- An older `canon.db` is deleted unread, including its `-wal` and `-shm` files, and
  history is re-ingested. There is no `ALTER TABLE` migration and no backup copy.
- A database newer than `SchemaVersion` is refused and left in place.
- `CREATE TABLE IF NOT EXISTS` does not change a table that already exists. It is not
  a migration.

## Values

- A canon column does not hold a JSON document. A missing row means the value was not
  reported, and it is not stored as zero.
- Text from outside the statement is bound with `TextExpr` or a SQLite blob literal.
