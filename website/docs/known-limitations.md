---
title: Known limitations
sidebar_label: Known limitations
---

## It is not a consistent snapshot

SqlDataPack reads each table with its own `SELECT` against the live source. It does not open a snapshot or a serializable transaction, so a slice taken under concurrent write load can be internally inconsistent across tables: invoices referencing customers that the customer read did not include. Both the foreign-key import order and the row-count check will pass it happily, because neither one checks for cross-table consistency, only that the rows that were read import cleanly and completely.

If you need a referentially consistent slice, export from a restored copy, a database snapshot, or a readable secondary instead of the primary.

## Global predicates fail open

Global WHERE predicates only apply to a table that has every column the predicate names; a table missing even one of them is exported unfiltered, every row. That is deliberate, so a shared lookup table is not silently emptied by a predicate written for tenant-scoped tables. The export warns once per unmatched table and stores the warning in the package, so a mistyped or inconsistent column name is visible in the result, but it is a warning and the export still succeeds. See [Options](/options) for how to scope a predicate so it cannot fail open.

## Two source tables can claim the same SQLite table

Each source table gets a SQLite table whose name is the schema and table name lowercased, with every character that is not a letter or digit replaced by `_`. So `dbo.Order-Items` and `dbo.Order_Items` both want `dbo__order_items`, and under a case-sensitive server collation so do `dbo.Orders` and `dbo.ORDERS`. The export refuses rather than guessing which table wins: planning fails with an error naming both source tables and the name they collapsed to. Exclude one of them from the export scope.

Planning fails the same way when a generated name lands in a reserved namespace. SQLite refuses any table name beginning `sqlite_`, and SqlDataPack keeps `zsdp_` for the package's own metadata tables, so a source schema named `sqlite` or `zsdp` (or a `DataTablePrefix` of `"sqlite"` or `"zsdp"`) is rejected with an error naming the source table. Set `DataTablePrefix` to move the exported tables out of the reserved namespace, or exclude the table.

See [Comparison](/comparison) for when to reach for something else instead.
