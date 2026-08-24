-- Source database for the dacpac tests: DacpacScopeAndDeployTests (scope, deploy, columnstore) and
-- DacpacEditAndDropTests (package editing, drop options). Two independent object groups, one file, so
-- both suites can run the whole script as their source.
--
-- Group 1 pairs with dacpac-target-with-extras.sql: SelectedParent / RegionLookup / SelectedChild match
-- it column for column, minus FK_SelectedChild_RegionLookup and IX_SelectedChild_RegionLookupId, which
-- are target-only extras. dbo.LegacyStagingImport is deliberately absent here for the same reason.
-- CatalogArchiveLog is referenced by nothing, so the edit test can lift it out of the model.
--
-- Group 2 is the scope catalog: a non-dbo schema, a sequence-backed default, a scalar function behind a
-- computed column, an in-scope FK and a cross-scope one, an auto-named temporal pair, and a columnstore
-- fact seeded past the default batch size.

-- ---------------------------------------------------------------- group 1: edit / drop objects

CREATE TABLE dbo.SelectedParent
(
    ParentId   INT IDENTITY(1,1) NOT NULL,
    ParentCode NVARCHAR(20) NOT NULL,
    ParentName NVARCHAR(50) NOT NULL,
    CONSTRAINT PK_SelectedParent PRIMARY KEY CLUSTERED (ParentId),
    CONSTRAINT UQ_SelectedParent_Code UNIQUE (ParentCode),
    CONSTRAINT CK_SelectedParent_Code CHECK (LEN(ParentCode) > 0)
);

CREATE TABLE dbo.RegionLookup
(
    RegionLookupId INT IDENTITY(1,1) NOT NULL,
    RegionCode     NVARCHAR(10) NOT NULL,
    CONSTRAINT PK_RegionLookup PRIMARY KEY CLUSTERED (RegionLookupId)
);
GO

CREATE FUNCTION dbo.fn_ComputeExtendedPrice(@qty INT, @unitPrice DECIMAL(18, 2))
RETURNS DECIMAL(18, 2)
AS
BEGIN
    RETURN @qty * @unitPrice;
END;
GO

-- No FK to RegionLookup and no index on RegionLookupId: both live only on the target.
CREATE TABLE dbo.SelectedChild
(
    ChildId        INT IDENTITY(1,1) NOT NULL,
    ParentId       INT            NOT NULL,
    Qty            INT            NOT NULL CONSTRAINT DF_SelectedChild_Qty DEFAULT 1,
    UnitPrice      DECIMAL(18, 2) NOT NULL,
    ExtendedPrice AS (dbo.fn_ComputeExtendedPrice(Qty, UnitPrice)),
    RegionLookupId INT NULL,
    CONSTRAINT PK_SelectedChild PRIMARY KEY CLUSTERED (ChildId),
    CONSTRAINT CK_SelectedChild_Qty CHECK (Qty > 0),
    CONSTRAINT FK_SelectedChild_SelectedParent FOREIGN KEY (ParentId) REFERENCES dbo.SelectedParent (ParentId)
);

-- Nothing references this table, so the edit test can remove it from the model and still load it.
CREATE TABLE dbo.CatalogArchiveLog
(
    CatalogArchiveLogId INT IDENTITY(1,1) NOT NULL,
    ArchivedAt          DATETIME2(3) NOT NULL CONSTRAINT DF_CatalogArchiveLog_ArchivedAt DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_CatalogArchiveLog PRIMARY KEY CLUSTERED (CatalogArchiveLogId)
);
GO

-- Parameterless and referencing nothing, so removing it leaves no dangling reference behind.
CREATE PROCEDURE dbo.usp_ArchiveCatalog
AS
BEGIN
    SET NOCOUNT ON;
    SELECT CAST(1 AS INT) AS Archived;
END;
GO

-- The survivor the edit test executes on the target to prove a deployed body actually runs.
CREATE PROCEDURE dbo.usp_CatalogSummary
AS
BEGIN
    SET NOCOUNT ON;
    SELECT COUNT(*) AS ChildCount FROM dbo.SelectedChild;
END;
GO

-- ---------------------------------------------------------------- group 2: scope catalog

CREATE SCHEMA inventory;
GO

CREATE SEQUENCE dbo.ProductNumberSequence AS INT START WITH 5000 INCREMENT BY 1;
GO

-- Backs the computed column on dbo.Products: a non-table dependency the selected-table walk has to find
-- on its own or the deployed table cannot be created.
CREATE FUNCTION dbo.NormalizeSku(@value NVARCHAR(20))
RETURNS NVARCHAR(20)
AS
BEGIN
    RETURN UPPER(LTRIM(RTRIM(@value)));
END;
GO

-- Never selected by a scoped export: the leak canary, and the far end of the cross-scope foreign key.
CREATE TABLE dbo.Suppliers
(
    SupplierId   INT IDENTITY(1,1) NOT NULL,
    SupplierName NVARCHAR(60) NOT NULL,
    CONSTRAINT PK_Suppliers PRIMARY KEY CLUSTERED (SupplierId)
);

CREATE TABLE inventory.Categories
(
    CategoryId   INT IDENTITY(1,1) NOT NULL,
    CategoryName NVARCHAR(60) NOT NULL,
    CONSTRAINT PK_Categories PRIMARY KEY CLUSTERED (CategoryId),
    CONSTRAINT UQ_Categories_Name UNIQUE (CategoryName)
);
GO

CREATE TABLE dbo.Products
(
    ProductId     INT NOT NULL CONSTRAINT DF_Products_ProductId DEFAULT (NEXT VALUE FOR dbo.ProductNumberSequence),
    CategoryId    INT NOT NULL,
    SupplierId    INT NOT NULL,
    Sku           NVARCHAR(20) NOT NULL,
    Qty           INT NOT NULL,
    UnitPrice     DECIMAL(18, 2) NOT NULL,
    NormalizedSku AS dbo.NormalizeSku(Sku),
    CONSTRAINT PK_Products PRIMARY KEY CLUSTERED (ProductId),
    CONSTRAINT UQ_Products_Sku UNIQUE (Sku),
    CONSTRAINT CK_Products_Qty CHECK (Qty > 0),
    CONSTRAINT FK_Products_Categories FOREIGN KEY (CategoryId) REFERENCES inventory.Categories (CategoryId),
    CONSTRAINT FK_Products_Suppliers FOREIGN KEY (SupplierId) REFERENCES dbo.Suppliers (SupplierId)
);

CREATE INDEX IX_Products_CategoryId ON dbo.Products (CategoryId);
GO

-- System-versioned with no HISTORY_TABLE clause, so SQL Server picks the name
-- MSSQL_TemporalHistoryFor_<object_id> -- knowable only at runtime, which is the point.
CREATE TABLE dbo.ProductPrices
(
    ProductPriceId INT NOT NULL,
    Sku            NVARCHAR(20) NOT NULL,
    ListPrice      DECIMAL(18, 2) NOT NULL,
    ValidFrom      DATETIME2(7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo        DATETIME2(7) GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo),
    CONSTRAINT PK_ProductPrices PRIMARY KEY CLUSTERED (ProductPriceId)
)
WITH (SYSTEM_VERSIONING = ON);
GO

-- Fact-shaped and clustered columnstore. Seeded well past the default 1,000-row batch so the export
-- writer and the import bulk copy both run several batches.
CREATE TABLE dbo.SalesFact
(
    SaleId    INT NOT NULL,
    ProductId INT NOT NULL,
    SaleDate  DATE NOT NULL,
    Quantity  INT NOT NULL,
    Amount    DECIMAL(18, 2) NOT NULL,
    INDEX CCI_SalesFact CLUSTERED COLUMNSTORE
);
GO

-- @@SEED
INSERT INTO dbo.SelectedParent (ParentCode, ParentName)
VALUES (N'P1', N'Parent One'),
       (N'P2', N'Parent Two');

INSERT INTO dbo.RegionLookup (RegionCode)
VALUES (N'EU'), (N'US');

INSERT INTO dbo.SelectedChild (ParentId, Qty, UnitPrice, RegionLookupId)
VALUES (1, 2, 12.50, 1),
       (1, 3, 4.00, 2),
       (2, 1, 99.99, NULL);

INSERT INTO dbo.Suppliers (SupplierName)
VALUES (N'North Supply'), (N'South Supply');

INSERT INTO inventory.Categories (CategoryName)
VALUES (N'Tools'), (N'Paint');

-- ProductId is supplied explicitly so the sequence stays unconsumed on the source: a row inserted after
-- import therefore has to land on the sequence's START WITH value, or the default did not travel.
INSERT INTO dbo.Products (ProductId, CategoryId, SupplierId, Sku, Qty, UnitPrice)
VALUES (1, 1, 1, N'sku-hammer', 4, 12.50),
       (2, 1, 2, N'sku-wrench', 7, 19.95),
       (3, 2, 1, N'sku-primer', 2, 8.00);

INSERT INTO dbo.ProductPrices (ProductPriceId, Sku, ListPrice)
VALUES (1, N'sku-hammer', 12.50),
       (2, N'sku-wrench', 19.95);

-- Two updates, two history rows in the auto-named history table. The WAITFOR forces a real
-- clock tick so the closed row versions get a non-zero period; SQL Server hides zero-duration
-- history rows from FOR SYSTEM_TIME queries.
WAITFOR DELAY '00:00:00.050';
UPDATE dbo.ProductPrices SET ListPrice = 13.50 WHERE ProductPriceId = 1;
UPDATE dbo.ProductPrices SET ListPrice = 21.00 WHERE ProductPriceId = 2;

INSERT INTO dbo.SalesFact (SaleId, ProductId, SaleDate, Quantity, Amount)
SELECT TOP (5000)
       numbers.n,
       1 + (numbers.n % 3),
       DATEADD(DAY, numbers.n % 365, CAST('2023-01-01' AS DATE)),
       1 + (numbers.n % 17),
       CAST(numbers.n AS DECIMAL(18, 2)) * 1.25
FROM (
    SELECT ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n
    FROM sys.all_columns a
    CROSS JOIN sys.all_columns b
) AS numbers
ORDER BY numbers.n;
