-- Built through dynamic SQL and QUOTENAME so this file itself stays plain ASCII regardless of
-- what bytes the identifiers carry.
-- DDL and seed are two batches separated by GO (SqlServerFixtureDatabase.ExecuteSqlAsync splits on it),
-- so each half declares the variables it uses. That is also what lets LoadDdl build the unseeded import
-- target out of this same file.
DECLARE @schemaName sysname = N'Facturaci' + NCHAR(0xF3) + N'n';        -- Facturaci[o with acute]n
DECLARE @bracketTable sysname = N'Env' + NCHAR(0xED) + N'o]Detalle';     -- Env[i with acute]o]Detalle
DECLARE @semicolonTable sysname = N'Cliente;Referencia';
DECLARE @quoteVersionedTable sysname = N'Tarifa''s Log';
DECLARE @quoteHistoryTable sysname = N'Tarifa''s Log_Archive';
DECLARE @sql NVARCHAR(MAX);

SET @sql = N'CREATE SCHEMA ' + QUOTENAME(@schemaName);
EXEC (@sql);

-- Table name with an embedded right bracket; columns with an embedded double quote and an
-- embedded single quote.
SET @sql = N'CREATE TABLE ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@bracketTable) + N' (
    EnvioId INT IDENTITY(1,1) PRIMARY KEY,
    ' + QUOTENAME(N'Recipient "Name"') + N' NVARCHAR(100) NOT NULL,
    ' + QUOTENAME(N'Note''s') + N' NVARCHAR(100) NULL
)';
EXEC (@sql);

-- Column with an embedded newline, added separately so the CREATE TABLE text above stays legible.
SET @sql = N'ALTER TABLE ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@bracketTable)
    + N' ADD ' + QUOTENAME(N'Line' + NCHAR(10) + N'Break') + N' NVARCHAR(50) NULL';
EXEC (@sql);

-- Table name with an embedded semicolon.
SET @sql = N'CREATE TABLE ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@semicolonTable) + N' (
    ClienteReferenciaId INT IDENTITY(1,1) PRIMARY KEY,
    Codigo NVARCHAR(50) NOT NULL
)';
EXEC (@sql);

-- Table and column names sitting exactly at the 128-character sysname limit.
CREATE TABLE dbo.[ShipmentReceiptReconciliationArchiveRecordAtTheSysnameLimit_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX]
(
    Id INT IDENTITY(1,1) PRIMARY KEY,
    [OriginatingLegacySystemReferenceCodeAtTheSysnameLimitColumn_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX] NVARCHAR(50) NOT NULL
);

-- System-versioned table whose current AND history table names both contain a single quote --
-- the temporal manager's OBJECT_ID('...') literal has to double it, distinct from the
-- bracket-doubling used for the identifier itself.
SET @sql = N'CREATE TABLE ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@quoteVersionedTable) + N' (
    TarifaId INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_TarifaLog PRIMARY KEY CLUSTERED,
    TarifaName NVARCHAR(100) NOT NULL,
    ValidFrom DATETIME2(7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo DATETIME2(7) GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@quoteHistoryTable) + N'))';
EXEC (@sql);


GO
-- @@SEED
DECLARE @schemaName sysname = N'Facturaci' + NCHAR(0xF3) + N'n';
DECLARE @bracketTable sysname = N'Env' + NCHAR(0xED) + N'o]Detalle';
DECLARE @semicolonTable sysname = N'Cliente;Referencia';
DECLARE @quoteVersionedTable sysname = N'Tarifa''s Log';
DECLARE @sql NVARCHAR(MAX);

SET @sql = N'INSERT INTO ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@bracketTable)
    + N' (' + QUOTENAME(N'Recipient "Name"') + N', ' + QUOTENAME(N'Note''s') + N', ' + QUOTENAME(N'Line' + NCHAR(10) + N'Break') + N')'
    + N' VALUES (N''Alex "Buyer" Rivera'', N''ships Tuesday'', N''first' + NCHAR(10) + N'second'')';
EXEC sp_executesql @sql;

SET @sql = N'INSERT INTO ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@bracketTable)
    + N' (' + QUOTENAME(N'Recipient "Name"') + N') VALUES (N''No quotes needed here'')';
EXEC sp_executesql @sql;

SET @sql = N'INSERT INTO ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@semicolonTable) + N' (Codigo) VALUES (N''REF-001''), (N''REF-002'')';
EXEC sp_executesql @sql;

INSERT INTO dbo.[ShipmentReceiptReconciliationArchiveRecordAtTheSysnameLimit_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX]
    ([OriginatingLegacySystemReferenceCodeAtTheSysnameLimitColumn_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX])
VALUES (N'at the limit');

SET @sql = N'INSERT INTO ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@quoteVersionedTable) + N' (TarifaName) VALUES (N''Standard''), (N''Premium'')';
EXEC sp_executesql @sql;
WAITFOR DELAY '00:00:00.050';
SET @sql = N'UPDATE ' + QUOTENAME(@schemaName) + N'.' + QUOTENAME(@quoteVersionedTable) + N' SET TarifaName = N''Standard-2'' WHERE TarifaName = N''Standard''';
EXEC sp_executesql @sql;
