-- =====================================================================
-- Import-target variants, one per named section. A test deploys its own
-- source fixture unseeded, then applies exactly one section on top, so
-- the target differs from the source in exactly one way.
--
-- Sections are loaded by name through TargetSchemaScripts.Variants, so
-- every name below has to match a constant there.
--
-- Every ALTER/CREATE targets the source table's exact name -- import
-- matches package tables to target tables by schema.table name, and
-- ImportOptions has no rename/mapping option.
--
-- Sections never share a database. ExtraAllowedColumns and
-- ExtraRequiredColumn both alter dbo.CustomerProfiles in ways that
-- cannot coexist, and ExtraRequiredColumn's NOT NULL/no-default column
-- would poison every copy into the database, not just the one it is
-- meant to poison.
--
-- No seed data anywhere: every table here is a target, and import
-- requires it empty.
-- =====================================================================


-- @@SECTION DatePrecisionCollapse
-- Pairs with type-vault.dbo.ChronoExtremes: same three column names,
-- narrowed to a smaller datetime2 precision than the source carries.
ALTER TABLE dbo.ChronoExtremes ALTER COLUMN Dt2Precision7 DATETIME2(3) NOT NULL;
ALTER TABLE dbo.ChronoExtremes ALTER COLUMN RegularDt DATETIME2(3) NOT NULL;
ALTER TABLE dbo.ChronoExtremes ALTER COLUMN SmallDt DATETIME2(0) NOT NULL;


-- @@SECTION TypeDrift
-- Pairs with type-vault.dbo.DriftSamples: same column names, all three
-- narrowed.
ALTER TABLE dbo.DriftSamples ALTER COLUMN RecordedAt DATETIME2(3) NOT NULL;
ALTER TABLE dbo.DriftSamples ALTER COLUMN Description VARCHAR(100) NOT NULL;
ALTER TABLE dbo.DriftSamples ALTER COLUMN Amount DECIMAL(18, 2) NOT NULL;


-- @@SECTION CollationSwap
-- Pairs with type-vault.dbo.FixedWidthTexts: CHAR/NCHAR and
-- VARCHAR/NVARCHAR deliberately swapped column-for-column.
ALTER TABLE dbo.FixedWidthTexts ALTER COLUMN CodeChar NCHAR(10) NOT NULL;
ALTER TABLE dbo.FixedWidthTexts ALTER COLUMN CodeNChar CHAR(10) NOT NULL;
ALTER TABLE dbo.FixedWidthTexts ALTER COLUMN LabelVarchar NVARCHAR(50) NOT NULL;
ALTER TABLE dbo.FixedWidthTexts ALTER COLUMN LabelNVarchar VARCHAR(50) NOT NULL;


-- @@SECTION DefaultedNullables
-- Pairs with core-commerce.dbo.CustomerDocuments: same nullable columns,
-- four now DEFAULTed -- a NULL sent by the package must still land as
-- NULL, not fall through to the DEFAULT.
ALTER TABLE dbo.CustomerDocuments ADD CONSTRAINT DF_CustomerDocuments_Label DEFAULT (N'untitled') FOR Label;
ALTER TABLE dbo.CustomerDocuments ADD CONSTRAINT DF_CustomerDocuments_PageCount DEFAULT (1) FOR PageCount;
ALTER TABLE dbo.CustomerDocuments ADD CONSTRAINT DF_CustomerDocuments_Amount DEFAULT (0) FOR Amount;
ALTER TABLE dbo.CustomerDocuments ADD CONSTRAINT DF_CustomerDocuments_IsVerified DEFAULT (0) FOR IsVerified;


-- @@SECTION ThirdTableIncompatible
-- Pairs with core-commerce.dbo.OrderLines as the deliberately incompatible
-- third table in a three-table import (Customers -> Orders -> OrderLines):
-- the copy has to die on OrderLines after Customers and Orders have
-- already committed.
--
-- It has to be a narrowed type, not an extra NOT NULL column: import
-- validates every table's columns before it copies anything, so an extra
-- required column is rejected up front and nothing is ever committed.
-- Types are the thing validation never looks at, so this is what gets
-- past it and then fails inside the bulk copy. Notes holds 'line-2' and
-- 'line-3', so NVARCHAR(3) truncates.
ALTER TABLE dbo.OrderLines ALTER COLUMN Notes NVARCHAR(3) NULL;


-- @@SECTION ExtraAllowedColumns
-- Pairs with core-commerce.dbo.CustomerProfiles (LegacyFlags excluded at
-- export either way): a surrogate identity the package never carries,
-- plus a nullable, a defaulted, a computed and a rowversion extra --
-- every allowed kind except generated-always, covered separately by
-- TemporalTargetForPlainSource below.
--
-- A table gets one identity column, and the source's CustomerProfileId
-- already is one, so it is rebuilt as a plain INT primary key first.
-- The package carries its values and bulk copy writes them explicitly
-- (KeepIdentity), so the row compare is unaffected -- this just frees
-- the table's identity slot for SurrogateId, which is the extra the
-- rule has to exempt on is_identity rather than on nullable/defaulted.
DECLARE @pk sysname = (
    SELECT name FROM sys.key_constraints
    WHERE parent_object_id = OBJECT_ID('dbo.CustomerProfiles') AND type = 'PK'
);
DECLARE @dropPk nvarchar(max) = N'ALTER TABLE dbo.CustomerProfiles DROP CONSTRAINT ' + QUOTENAME(@pk);
EXEC(@dropPk);
ALTER TABLE dbo.CustomerProfiles DROP COLUMN CustomerProfileId;
GO
ALTER TABLE dbo.CustomerProfiles ADD CustomerProfileId INT NOT NULL CONSTRAINT PK_CustomerProfiles PRIMARY KEY;
GO

ALTER TABLE dbo.CustomerProfiles ADD SurrogateId INT IDENTITY(1,1) NOT NULL CONSTRAINT UQ_CustomerProfiles_SurrogateId UNIQUE;
ALTER TABLE dbo.CustomerProfiles ADD NullableExtra NVARCHAR(50) NULL;
ALTER TABLE dbo.CustomerProfiles ADD DefaultedExtra INT NOT NULL CONSTRAINT DF_CustomerProfiles_DefaultedExtra DEFAULT (42);
ALTER TABLE dbo.CustomerProfiles ADD ComputedExtra AS (LEN(DisplayName));
ALTER TABLE dbo.CustomerProfiles ADD RowVersionExtra ROWVERSION;


-- @@SECTION ExtraRequiredColumn
-- Pairs with core-commerce.dbo.CustomerProfiles: same base shape, plus
-- one NOT NULL extra with no default. Validation must reject this before
-- any row is copied.
ALTER TABLE dbo.CustomerProfiles ADD RequiredExtra INT NOT NULL;


-- @@SECTION MissingChildTable
-- Pairs with core-commerce's dbo.Customers -> dbo.CustomerProfiles pair:
-- the child table is simply absent from the target. Nothing references
-- CustomerProfiles, so it drops without touching anything else, and
-- dbo.Customers survives to prove validation ran before any copy.
DROP TABLE dbo.CustomerProfiles;


-- @@SECTION MissingColumn
-- Pairs with core-commerce.dbo.Customers: every table present, one
-- packaged column missing. Notes is nullable with no constraint or
-- index on it, so dropping it leaves the rest of the table intact.
ALTER TABLE dbo.Customers DROP COLUMN Notes;


-- @@SECTION TemporalTargetForPlainSource
-- Pairs with temporal-suite.dbo.Ledgers (plain, non-temporal): the target
-- IS system-versioned, exercising the generated-always extras path and
-- the plain-source-into-temporal-target path at once. Applied to an empty
-- database on its own -- temporal-suite's own dbo.Ledgers script must NOT
-- be deployed alongside it, since this replaces the table rather than
-- altering it.
CREATE TABLE dbo.Ledgers
(
    LedgerId  INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_Ledgers PRIMARY KEY CLUSTERED,
    Note      NVARCHAR(100) NOT NULL,
    ValidFrom DATETIME2(7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo   DATETIME2(7) GENERATED ALWAYS AS ROW END   NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.LedgerHistory));
