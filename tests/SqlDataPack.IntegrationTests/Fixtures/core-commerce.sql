CREATE TABLE dbo.Countries
(
    CountryId   INT IDENTITY(1,1) PRIMARY KEY,
    IsoCode     CHAR(2)       NOT NULL,
    CountryName NVARCHAR(100) NOT NULL
);

CREATE TABLE dbo.Currencies
(
    CurrencyId   INT IDENTITY(1,1) PRIMARY KEY,
    IsoCode      CHAR(3)      NOT NULL,
    CurrencyName NVARCHAR(50) NOT NULL
);

-- TenantId + IsActive are the WHERE-clause gating columns; both live here, only TenantId on
-- Orders, neither on GlobalSettings -- exactly the three-way split the filter tests need.
CREATE TABLE dbo.Customers
(
    CustomerId  INT IDENTITY(1,1) PRIMARY KEY,
    TenantId    INT              NOT NULL,
    IsActive    BIT              NOT NULL,
    ExternalId  UNIQUEIDENTIFIER NOT NULL,
    Name        NVARCHAR(100)    NOT NULL,
    CreditLimit DECIMAL(18, 2)   NOT NULL,
    CreatedAt   DATETIME2(3)     NOT NULL,
    Notes       NVARCHAR(200)    NULL,
    CountryId   INT              NULL,
    CONSTRAINT FK_Customers_Countries FOREIGN KEY (CountryId) REFERENCES dbo.Countries (CountryId)
);

-- DisplayName is the "keep" column, Nickname the "skip" column, LegacyFlags the unsupported
-- (sql_variant) column: excluding LegacyFlags is what makes this table exportable at all.
CREATE TABLE dbo.CustomerProfiles
(
    CustomerProfileId INT IDENTITY(1,1) PRIMARY KEY,
    CustomerId         INT           NOT NULL,
    DisplayName        NVARCHAR(100) NOT NULL,
    Nickname           NVARCHAR(100) NULL,
    LegacyFlags        SQL_VARIANT   NULL,
    CONSTRAINT FK_CustomerProfiles_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (CustomerId)
);

-- Every nullable scalar kind null-not-default cares about, in one table: nvarchar, int,
-- decimal, datetime2, varbinary, uniqueidentifier, bit.
CREATE TABLE dbo.CustomerDocuments
(
    CustomerDocumentId INT IDENTITY(1,1) PRIMARY KEY,
    CustomerId          INT              NOT NULL,
    Label                NVARCHAR(100)    NULL,
    PageCount            INT              NULL,
    Amount               DECIMAL(18, 4)   NULL,
    IssuedAt             DATETIME2(7)     NULL,
    ScanBytes            VARBINARY(64)    NULL,
    ExternalRef          UNIQUEIDENTIFIER NULL,
    IsVerified           BIT              NULL,
    CONSTRAINT FK_CustomerDocuments_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (CustomerId)
);

-- CHECK + FK here are the ones constraints-left-untrusted checks stay is_not_trusted after import.
CREATE TABLE dbo.Orders
(
    OrderId    INT IDENTITY(1,1) PRIMARY KEY,
    CustomerId INT            NOT NULL,
    TenantId   INT            NOT NULL,
    CurrencyId INT            NULL,
    OrderTotal DECIMAL(18, 2) NOT NULL,
    OrderedAt  DATETIME2(3)   NOT NULL,
    CONSTRAINT CK_Orders_OrderTotal CHECK (OrderTotal >= 0),
    CONSTRAINT FK_Orders_Customers FOREIGN KEY (CustomerId) REFERENCES dbo.Customers (CustomerId),
    CONSTRAINT FK_Orders_Currencies FOREIGN KEY (CurrencyId) REFERENCES dbo.Currencies (CurrencyId)
);

CREATE TABLE dbo.OrderLines
(
    OrderLineId   INT IDENTITY(1,1) PRIMARY KEY,
    OrderId       INT            NOT NULL,
    Qty           INT            NOT NULL,
    UnitPrice     DECIMAL(18, 2) NOT NULL,
    ExtendedPrice AS (Qty * UnitPrice),
    Notes         NVARCHAR(200)  NULL,
    CONSTRAINT FK_OrderLines_Orders FOREIGN KEY (OrderId) REFERENCES dbo.Orders (OrderId)
);

-- No gating columns at all: the where-clause-fails-open target.
CREATE TABLE dbo.GlobalSettings
(
    SettingId    INT IDENTITY(1,1) PRIMARY KEY,
    SettingName  NVARCHAR(50)  NOT NULL,
    SettingValue NVARCHAR(200) NULL
);

-- Mimics the table SSMS's "Database Diagrams" feature leaves behind: a regular user table
-- (is_ms_shipped = 0), excluded by name rather than by a system flag.
CREATE TABLE dbo.sysdiagrams
(
    name         NVARCHAR(128) NOT NULL,
    principal_id INT NOT NULL,
    diagram_id   INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
    version      INT NULL,
    definition   VARBINARY(MAX) NULL,
    CONSTRAINT UK_sysdiagrams_principal_name UNIQUE (principal_id, name)
);

EXEC('CREATE SCHEMA tenant');

-- Same table name as dbo.Customers in a different schema: cross-schema disambiguation, and
-- "Customer*" as a wildcard hits this, dbo.Customers, and dbo.CustomerProfiles/CustomerDocuments.
CREATE TABLE tenant.Customers
(
    TenantCustomerId INT IDENTITY(1,1) PRIMARY KEY,
    DisplayName      NVARCHAR(100) NOT NULL
);

CREATE TABLE tenant.Partners
(
    PartnerId   INT IDENTITY(1,1) PRIMARY KEY,
    PartnerName NVARCHAR(100) NOT NULL,
    CONSTRAINT UQ_Partners_Name UNIQUE (PartnerName)
);

-- @@SEED

INSERT INTO dbo.Countries (IsoCode, CountryName) VALUES ('US', N'United States'), ('NL', N'Netherlands'), ('DE', N'Germany');

INSERT INTO dbo.Currencies (IsoCode, CurrencyName) VALUES ('USD', N'US Dollar'), ('EUR', N'Euro'), ('GBP', N'British Pound');

-- Ascending, non-contiguous identity values -- including zero and a negative key -- so the next
-- natural insert continues cleanly above 100. Also carries the string hazards: embedded single
-- quote + brackets, embedded double quotes, a non-ASCII name, and NULL vs empty-string Notes.
SET IDENTITY_INSERT dbo.Customers ON;
INSERT INTO dbo.Customers (CustomerId, TenantId, IsActive, ExternalId, Name, CreditLimit, CreatedAt, Notes, CountryId) VALUES
    (-1,  1, 1, NEWID(), N'Legacy Zero-Adjacent Account', 0.00,                 '2020-01-01T00:00:00', NULL, 1),
    (0,   1, 1, NEWID(), N'Boundary Row',                 100.00,               '2020-01-02T00:00:00', N'',   1),
    (1,   1, 1, NEWID(), N'O''Brien [VIP]',                5000.00,             '2021-03-04T08:30:00', N'embedded quote and brackets', 1),
    (2,   1, 0, NEWID(), N'Acme "Prime" Corp',             2500.50,             '2021-05-06T09:15:00', N'embedded double quotes', 1),
    (5,   2, 1, NEWID(), NCHAR(0xE9) + N'clat Foods',      1200.00,             '2022-02-10T00:00:00', NULL, 2),
    (10,  2, 1, NEWID(), N'North Wind Traders',            15000.00,            '2022-06-01T12:00:00', N'regular customer', 2),
    (11,  2, 0, NEWID(), N'Contoso Retail',                8000.00,             '2022-07-15T12:00:00', NULL, 2),
    (12,  2, 1, NEWID(), N'Fabrikam Supplies',             3000.00,             '2022-09-20T12:00:00', NULL, 2),
    (13,  2, 1, NEWID(), N'Adventure Works',                4500.25,             '2022-11-05T12:00:00', NULL, 2),
    (14,  2, 0, NEWID(), N'Tailspin Toys',                    999.99,             '2023-01-11T12:00:00', NULL, 2),
    (20,  3, 1, NEWID(), N'Wide World Importers',            25000.00,            '2023-03-14T12:00:00', NULL, 3),
    (21,  3, 0, NEWID(), N'Proseware Inc',                      500.00,            '2023-04-18T12:00:00', NULL, 3),
    (50,  3, 1, NEWID(), N'Litware Distribution',              7500.00,            '2023-08-22T12:00:00', NULL, 3),
    (51,  3, 1, NEWID(), N'Blue Yonder Logistics',             11000.00,           '2023-09-30T12:00:00', NULL, 3),
    (100, 1, 1, NEWID(), N'Northgate Holdings',        99999999999999.99, '2024-01-01T00:00:00', N'credit limit boundary for DECIMAL(18,2)', 1);
SET IDENTITY_INSERT dbo.Customers OFF;

-- Volume for filter-predicate-consistency, progress-event-stream and batch-size-computation:
-- natural auto-increment continues from 101.
;WITH Numbers AS (
    SELECT TOP (200) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
)
INSERT INTO dbo.Customers (TenantId, IsActive, ExternalId, Name, CreditLimit, CreatedAt, Notes, CountryId)
SELECT 1 + (rn % 3),
       CASE WHEN rn % 4 = 0 THEN 0 ELSE 1 END,
       NEWID(),
       CONCAT(N'Bulk Customer ', rn),
       CAST(50 + (rn % 1000) AS DECIMAL(18, 2)),
       DATEADD(DAY, rn, '2023-01-01'),
       CASE WHEN rn % 5 = 0 THEN NULL ELSE CONCAT(N'note-', rn) END,
       1 + (rn % 3)
FROM Numbers;

INSERT INTO dbo.CustomerProfiles (CustomerId, DisplayName, Nickname, LegacyFlags) VALUES
    (1,   N'O''Brien Prime', N'VIP',      CAST(1 AS SQL_VARIANT)),
    (2,   N'Acme Prime',     NULL,        CAST(N'legacy-note' AS SQL_VARIANT)),
    (10,  N'North Wind',     N'NWT',      NULL),
    (100, N'Northgate',      N'flagship', CAST(42 AS SQL_VARIANT));

-- Fully populated, fully NULL, and partially NULL rows across every nullable column kind.
INSERT INTO dbo.CustomerDocuments (CustomerId, Label, PageCount, Amount, IssuedAt, ScanBytes, ExternalRef, IsVerified) VALUES
    (1, N'Signed Contract', 12, CAST(4500.1234 AS DECIMAL(18, 4)), CAST('2024-01-15T09:00:00.1234567' AS DATETIME2(7)), 0xDEADBEEF, '11111111-1111-1111-1111-111111111111', 1),
    (2, N'Invoice Scan',     3, CAST(210.5000  AS DECIMAL(18, 4)), CAST('2024-02-20T11:30:00.7654321' AS DATETIME2(7)), 0xCAFEBABE, '22222222-2222-2222-2222-222222222222', 0);
INSERT INTO dbo.CustomerDocuments (CustomerId) VALUES (1), (1), (2), (2), (10), (100);
INSERT INTO dbo.CustomerDocuments (CustomerId, Label, ScanBytes) VALUES (10, N'partial-a', 0x00), (100, NULL, 0xFFFF);

DECLARE @customerCount INT = (SELECT COUNT(*) FROM dbo.Customers);

-- Orders: exactly 500 rows (an exact multiple of common batch sizes).
;WITH Numbers AS (
    SELECT TOP (500) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS rn
    FROM sys.all_objects a CROSS JOIN sys.all_objects b
),
CustomerPool AS (
    SELECT CustomerId, TenantId, ROW_NUMBER() OVER (ORDER BY CustomerId) AS seq
    FROM dbo.Customers
)
INSERT INTO dbo.Orders (CustomerId, TenantId, CurrencyId, OrderTotal, OrderedAt)
SELECT cp.CustomerId,
       cp.TenantId,
       CASE WHEN n.rn % 7 = 0 THEN NULL ELSE ((n.rn % 3) + 1) END,
       CAST(n.rn * 3.33 AS DECIMAL(18, 2)),
       DATEADD(MINUTE, n.rn, '2024-01-01')
FROM Numbers n
JOIN CustomerPool cp ON cp.seq = ((n.rn - 1) % @customerCount) + 1;

-- OrderLines: 1-3 lines per order, so the total is deliberately not a round number.
;WITH OrderExpand AS (
    SELECT o.OrderId, v.LineNumber
    FROM dbo.Orders o
    CROSS APPLY (VALUES (1), (2), (3)) AS v(LineNumber)
    WHERE v.LineNumber <= 1 + (o.OrderId % 3)
)
INSERT INTO dbo.OrderLines (OrderId, Qty, UnitPrice, Notes)
SELECT OrderId,
       1 + (OrderId % 5),
       CAST(9.99 + (LineNumber * 1.10) AS DECIMAL(18, 2)),
       CASE WHEN LineNumber = 1 THEN NULL ELSE CONCAT(N'line-', LineNumber) END
FROM OrderExpand;

INSERT INTO dbo.GlobalSettings (SettingName, SettingValue) VALUES (N'Theme', N'Dark'), (N'Locale', N'en-US'), (N'RetentionDays', N'90');

INSERT INTO dbo.sysdiagrams (name, principal_id, version, definition) VALUES (N'Diagram_0', 1, 1, 0x0102);

INSERT INTO tenant.Customers (DisplayName) VALUES (N'Tenant Root Account'), (N'Tenant Secondary Account');

INSERT INTO tenant.Partners (PartnerName) VALUES (N'Reseller Alpha'), (N'Reseller Beta');
