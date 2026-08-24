---
title: Compared to the alternatives
sidebar_label: Compared to the alternatives
---

Native backup and restore is excellent at what it does. It is the fastest way to move a database, it is transactionally consistent, it preserves everything, and it is what you want for disaster recovery. A `.bacpac` is a good portable clone when you need schema and data together, and SqlPackage can already restrict *which tables* carry data, via `/p:TableData`.

Neither of them can drop a column, apply a `WHERE` clause, or give you a stage between extract and load where a script can rewrite values without going back to the source. `bcp` can do the first three from a query, at the cost of a command line per table and a pile of loose files. The table below is about what each tool can do; the sections after it are about what each one costs to actually do.

| You want to… | Native backup/restore | `.bacpac` / SqlPackage | `bcp` / `BULK INSERT` | **SqlDataPack** |
| --- | --- | --- | --- | --- |
| Pick which tables come out | Whole database | Yes, via `/p:TableData` | Yes, one command per table | Yes, include/exclude patterns |
| Drop columns | No | No | Yes, in the query | Yes, `ExcludeColumns` |
| Filter rows | No | No | Yes, in the query | Yes, global and per-table `WHERE` |
| Single file out | Yes | Yes | No, one file per table | Yes |
| **Query and inspect it without restoring to SQL Server** | No | No | No | Yes, it is a SQLite database |
| **Modify the data before restoring** | No | No | No | Yes, `UPDATE` in place; row counts are fixed for now |
| Fix a bad edit without touching production again | No | No | No | Yes, re-edit the file and import again into an emptied target |
| Nothing to install on the server | No | Needs SqlPackage | Needs `bcp` | Yes, one NuGet package |
| Hand it to an agent or a teammate with no DB access | No | No | No | Yes |
| Type metadata, row counts and FK order travel with it | Implicit | Schema only | No | Yes, in the manifest |
| Restores back into SQL Server | Yes, exact | Yes, with schema | Yes, `-E` keeps identity | Yes, identity kept, counts verified |
| Carries full schema | Yes | Yes | No | Optional (dacpac) |
| Consistent snapshot of the source | **Yes** | Only from a copy | No | **No** |
| Point-in-time restore and DR | **Yes** | No | No | No |

## It is not a consistent snapshot

SqlDataPack reads each table with its own `SELECT` against the live source. It does not open a snapshot or a serializable transaction, so a slice taken from a database under concurrent write load can be internally inconsistent across tables: invoices referencing customers that the customer read did not include. The foreign-key import order will load that slice happily, and the row-count check will pass it.

If you need a referentially consistent slice, export from a restored copy, a database snapshot, or a readable secondary rather than from the primary. (`.bacpac` carries the same caveat for the same reason.)

## Editing has a limit

You can change values in the package freely. `UPDATE` statements roundtrip through import. You cannot add or remove rows yet: import compares what it copies against `exported_row_count` in `zsdp_table_stats`, so a table you deleted from fails the check partway through the import, and the target has to be emptied before you can retry. Filter rows out at export with a `WHERE` clause instead. Lifting that for deliberate edits is tracked in [#18](https://github.com/zachtbeer/sqldatapack/issues/18) for 1.0.0. See [Editing the package](/editing-the-package) for the full rules.

This is a preview limitation, not the intended design. Full row editing lands before the 1.0.0 tag.

## The difference is what it costs to do

Most of what SqlDataPack does is *possible* with the tools above. The question is what each one asks of you.

`bcp … queryout` takes an arbitrary query, so it can select tables, project columns, filter rows, and transform values on the way out. But it does one table per invocation: a seven-table filtered slice is seven commands, seven output files, and format-file management, and at the end you hold a pile of flat files with no manifest, no type metadata, and no import order. Because the transform lives in the query, every revision to your masking rules is another read against production.

Restore-to-staging (restore a copy, run T-SQL against it, re-export) is more capable than either, and it asks for a spare instance, full-size disk, and usually a DBA.

The thing this actually replaces, in most teams, is the hand-rolled export script: the PowerShell or C# file someone wrote two years ago that pulls a handful of tables, hard-codes the table list, and has a masking section that was correct at the time. It goes stale silently. A column gets added and it is not scrubbed. A table gets added and nobody notices it is missing from the slice until a dev hits a null reference. Every fix means someone reading that script again. The other outcome is worse and more common: the slice is too much hassle, so nobody takes one, and people develop against seed data that does not reproduce the bug.

SqlDataPack is one NuGet package and one method call, from the application that already holds the connection string. Nothing is installed on the server, nothing is shelled out to, and the transform runs against a local, megabyte-scale file you can re-run until it is right without touching the source again. That is the actual claim: not that the alternatives cannot do this, but that this is the version a developer runs on a laptop, in a CI job, or behind an admin endpoint, without asking anyone for anything.

## When to use something else

Reach for native backup and restore when you want a faithful clone or point-in-time recovery. Reach for `.bacpac`/SqlPackage when you want a full database with its schema. If you already have the scratch instance and the disk, restore-to-staging plus T-SQL gives you real constraints, real types, and set-based `DELETE`. [dbatools](https://dbatools.io) ships `Invoke-DbaDbDataMasking` with a classifier if masking is all you need. See [Known limitations](/known-limitations) for the hard edges.
