---
title: Support matrix
sidebar_label: Support matrix
---

This page describes what the current pre-release of SqlDataPack targets, tests, and expects. The target surface may change before the first stable release.

## Summary

SqlDataPack supports .NET `net8.0` and `net10.0`, exports SQL Server table data into a versioned local package, and imports that package back into prepared SQL Server tables.

It does not try to replace SQL Server backup/restore, replication, incremental sync, merge/upsert workflows, or unrestricted schema migration.

## .NET

| Area | Support |
| --- | --- |
| Target frameworks | `net8.0`, `net10.0` |
| Package type | .NET class library |
| Primary package manager | NuGet |
| Test coverage | Unit, fuzz, and SQL Server integration suites run against both target frameworks |

## SQL Server

| Area | Support |
| --- | --- |
| Integration test target | SQL Server in Testcontainers |
| CI database coverage | Docker-backed SQL Server integration tests run in the `integration` job of `.github/workflows/ci.yml` on `ubuntu-latest` |
| Expected compatibility | SQL Server versions that support the system catalog and `sys.dm_db_partition_stats` queries used by export planning |
| Older SQL Server note | Table size estimates are expected to work through `sys.dm_db_partition_stats`; if permissions block those estimates, export falls back with a warning and still copies rows |

SqlDataPack validates behavior against the SQL Server container used by the integration test suite. Test other SQL Server versions in your own environment before relying on them.

## Operating Systems

| Area | Support |
| --- | --- |
| CI OS | `ubuntu-latest` |
| Local development | macOS, Linux, or Windows with supported .NET SDKs |
| Integration tests | Require Docker and enough memory to start SQL Server |

## SQLite Files

| Area | Support |
| --- | --- |
| File format | Versioned and owned by SqlDataPack |
| Inspection | Use `SqlDataPackReader` for supported metadata reads |
| Direct `zsdp_*` queries | Useful for debugging, but not a public compatibility contract |

Packages are intended to be portable SQL Server data snapshots. They can be inspected locally, shared with support teams, or used as database context for AI coding agents.

## Out Of Scope

- SQL Server-native backup/restore replacement
- Incremental sync
- Merges or upserts into existing target rows
- Constraint disabling/re-enabling around import
- Full schema migration without optional dacpac deployment
- Support for every SQL Server data type
