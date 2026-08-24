---
title: Supported types
sidebar_label: Supported types
---

```text
SQL Server        SQLite
-----------        ------
int, bigint   -->  INTEGER
nvarchar      -->  TEXT
```

The package stores values using SQLite affinities chosen for reliable transport, not a one-to-one type mapping. The original SQL Server type name is kept alongside every column, so import can convert each value back into what `SqlBulkCopy` expects for its real type.

## The matrix

| Category | SQL Server types | SQLite storage |
| --- | --- | --- |
| Integer and boolean | `bigint`, `int`, `smallint`, `tinyint`, `bit` | `INTEGER` |
| Floating point | `float`, `real` | `REAL` |
| Text | `char`, `varchar`, `text`, `nchar`, `nvarchar`, `ntext` | `TEXT` |
| Date/time | `date`, `datetime`, `datetime2`, `datetimeoffset`, `smalldatetime`, `time` | `TEXT` |
| Numeric, text-preserved | `decimal`, `numeric`, `money`, `smallmoney` | `TEXT` |
| XML, text-preserved | `xml` | `TEXT` |
| JSON, text-preserved | native `json` (SQL Server 2025 and Azure SQL) | `TEXT` |
| Vector embeddings | native `vector` (`float32` base type, GA on Azure SQL Database, Azure SQL Managed Instance, SQL Server 2025, and Fabric SQL DB; preview `float16` base type also supported) | `TEXT` (JSON array) |
| Binary | `binary`, `varbinary`, `image` | `BLOB` |
| Identifiers | `uniqueidentifier` | `TEXT` |
| Server-generated | `timestamp`, `rowversion` | `BLOB` (captured for inspection, skipped on import; SQL Server generates a fresh value on the target) |

XML columns round-trip through SQLite `TEXT`. If a package is edited and an XML value is no longer valid XML, import fails with SQL Server's own XML conversion error.

## Vector columns

`vector` columns are stored as SQLite `TEXT` JSON arrays, with the base type and dimension count recorded alongside the column so import can reconstruct the native value.

- **`float32`**, the GA base type, round-trips bit-for-bit through the native binary `SqlVector<float>` transport.
- The preview **`float16`** base type round-trips through its JSON representation instead. Import requires the target database to have `PREVIEW_FEATURES` enabled, and export emits a warning whenever a `float16` column is present.

Either way, import requires the matching `vector(N[, float16])` column to already exist on the target; SqlDataPack does not create it for you.

## JSON columns

Native SQL Server `json` columns are stored as SQLite `TEXT` and imported back into SQL Server `json` columns.

JSON that already lives in `nvarchar`, `varchar`, or another text column is not treated specially: it round-trips as ordinary text, the same as any other string value.

If a package is edited and a native JSON value is no longer valid JSON, import fails with SQL Server's own JSON validation error.

## Unsupported types

`sql_variant`, `geography`, `geometry`, and `hierarchyid` are not supported. A table carrying one of these fails export preflight rather than exporting a column SqlDataPack cannot faithfully convert.

Exclude the column with `ExcludeColumns` (`schema.table.column`) to export the rest of the table:

```csharp
options.ExcludeColumns = ["dbo.Stores.Location"]; // a geography column
```

See [Options](/options) for `ExcludeColumns`, and [Troubleshooting](/troubleshooting) for what the preflight failure looks like and how to work through it.
