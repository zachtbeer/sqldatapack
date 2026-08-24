---
slug: /
title: SqlDataPack
sidebar_label: Overview
sidebar_position: 1
---

SqlDataPack is a .NET library that exports a slice of a SQL Server database into a single SQLite file, lets you edit it, and lets you import it back into SQL Server.

```csharp
using SqlDataPack;

// Export: SQL Server to one SQLite file
await SqlData.ExportAsync(sourceConnectionString, "database.sqlite");

// Import: that file back into SQL Server
await SqlData.ImportAsync("database.sqlite", targetConnectionString);
```

The file in between is ordinary SQLite, so between those two calls you can open it, query it, scrub it, or reshape it, before it goes anywhere near SQL Server again.

```text
   SQL Server  ──export──▶  .sqlite package  ──import──▶  SQL Server
   (production)                    │                      (dev / test / lab)
                                   │
                                   ▼
                     ordinary SQLite, so before it lands,
                query it · scrub it · reshape it · hand it to an agent

     selected tables, columns and rows only  ·  identity and row counts verified
   recipient needs no credentials  ·  no calls beyond the SQL Server you name
```

## Why not just take a backup?

Native backup and restore is excellent at what it does. It is the fastest way to move a database, it is transactionally consistent, it preserves everything, and it is what you want for disaster recovery. A `.bacpac` is a good portable clone when you need schema and data together, and SqlPackage can already restrict *which tables* carry data, via `/p:TableData`.

Neither of them can drop a column, apply a `WHERE` clause, or give you a stage between extract and load where a script can rewrite values without going back to the source. `bcp` can do the first three from a query, at the cost of a command line per table and a pile of loose files. The table below is about capability; the section after it is about what each one costs to actually do.

| You want to… | Native backup/restore | `.bacpac` / SqlPackage | `bcp` / `BULK INSERT` | **SqlDataPack** |
| --- | --- | --- | --- | --- |
| Pick which tables come out | Whole database | Yes, via `/p:TableData` | Yes, one command per table | Yes, include/exclude patterns |
| Drop columns | No | No | Yes, in the query | Yes, `ExcludeColumns` |
| Filter rows | No | No | Yes, in the query | Yes, global and per-table `WHERE` |
| Single file out | Yes | Yes | No, one file per table | Yes |
| **Query and inspect it without restoring to SQL Server** | No | No | No | Yes, it is a SQLite database |
| **Modify the data before restoring** | No | No | No | Yes, plain SQL against the file |
| Fix a bad edit without touching production again | No | No | No | Yes, edit the file and retry |
| Nothing to install on the server | No | Needs SqlPackage | Needs `bcp` | Yes, one NuGet package |
| Hand it to an agent or a teammate with no DB access | No | No | No | Yes |
| Type metadata, row counts and FK order travel with it | Implicit | Schema only | No | Yes, in the manifest |
| Restores back into SQL Server | Yes, exact | Yes, with schema | Yes, `-E` keeps identity | Yes, identity kept, counts verified |
| Carries full schema | Yes | Yes | No | Optional (dacpac) |
| Consistent snapshot of the source | **Yes** | Only from a copy | No | **No** |
| Point-in-time restore and DR | **Yes** | No | No | No |

Most of what SqlDataPack does is possible with the tools above; the question is what each one asks of you. `bcp … queryout` can select tables, drop columns, and filter rows, but it runs one table at a time: a seven-table filtered slice is seven commands, seven output files, and at the end you hold a pile of flat files with no manifest, no type metadata, and no import order. Restore-to-staging (restore a copy, run T-SQL against it, re-export) is more capable than either, but it asks for a spare instance, full-size disk, and usually a DBA.

SqlDataPack is one NuGet package and one method call, from the application that already holds the connection string. Nothing is installed on the server and nothing is shelled out to; the transform runs against a local, megabyte-scale file you can re-run until it is right without touching the source again.

## When to use something else

Reach for native backup/restore when you want a faithful clone or point-in-time recovery. Reach for `.bacpac`/SqlPackage when you want a full database with its schema. If you already have the scratch instance and the disk, restore-to-staging plus T-SQL gives you real constraints, real types, and set-based `DELETE`.

See [Known limitations](/known-limitations) for the hard edges.

## Use it for

- Give a dev, test, QA, or demo environment a filtered, scrubbed slice instead of a full production copy.
- Send a small, scoped snapshot along with a support issue.
- Give an AI coding agent a local, queryable copy of relevant SQL Server tables instead of database credentials.
- Package reproducible database state for bug reports and regression tests.
- Inspect SQL Server data on a machine that does not have SQL Server installed.

## Next

Start with [Getting started](/getting-started).
