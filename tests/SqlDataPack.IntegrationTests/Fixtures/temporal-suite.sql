-- Canonical pair: the textbook Department/DepartmentHistory shape, no FK.
CREATE TABLE dbo.Departments
(
    DepartmentId   INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Departments PRIMARY KEY CLUSTERED,
    DepartmentName NVARCHAR (100) NOT NULL,
    ManagerId      INT NULL,
    ValidFrom      DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo        DATETIME2 (7) GENERATED ALWAYS AS ROW END   NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.DepartmentHistory));

-- FK to a non-temporal parent: the suspend/restore ceremony must coexist with normal FK ordering.
CREATE TABLE dbo.Offices
(
    OfficeId   INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Offices PRIMARY KEY CLUSTERED,
    OfficeName NVARCHAR (100) NOT NULL
);

CREATE TABLE dbo.Workers
(
    WorkerId   INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Workers PRIMARY KEY CLUSTERED,
    OfficeId   INT NOT NULL CONSTRAINT FK_Workers_Offices FOREIGN KEY REFERENCES dbo.Offices (OfficeId),
    WorkerName NVARCHAR (100) NOT NULL,
    ValidFrom  DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo    DATETIME2 (7) GENERATED ALWAYS AS ROW END   NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.WorkerHistory));

-- Hidden period columns: still readable from sys.columns, must stay HIDDEN after a round trip.
CREATE TABLE dbo.Flags
(
    FlagId    INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Flags PRIMARY KEY CLUSTERED,
    FlagName  NVARCHAR (100) NOT NULL,
    ValidFrom DATETIME2 (7) GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    ValidTo   DATETIME2 (7) GENERATED ALWAYS AS ROW END   HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.FlagHistory));

-- Custom period column names, finite retention, underscore-suffixed history table: a real-world
-- shape distinct from the default-named tables above.
CREATE TABLE dbo.Subscriptions
(
    SubscriptionId    INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Subscriptions PRIMARY KEY CLUSTERED,
    CustomerId        INT NOT NULL,
    PlanName          NVARCHAR (100) NOT NULL,
    LastUpdateDate    DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
    LastUpdateValidTo DATETIME2 (7) GENERATED ALWAYS AS ROW END   NOT NULL,
    PERIOD FOR SYSTEM_TIME (LastUpdateDate, LastUpdateValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.Subscription_History, HISTORY_RETENTION_PERIOD = 3 MONTHS));

-- Plain, non-temporal source. Paired at import time with narrow-target-variants.dbo.LedgersTemporal:
-- the importer must leave versioning on and let the engine auto-populate the period.
CREATE TABLE dbo.Ledgers
(
    LedgerId INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Ledgers PRIMARY KEY CLUSTERED,
    Note     NVARCHAR (100) NOT NULL
);

-- Five identically-shaped, independent pairs, named so their ordinal (case-insensitive,
-- alphabetical) walk order is exactly: Districts(1), Regions(2), Sectors(3), Teams(4),
-- Territories(5). Regions/Teams cover temporal-multi-and-fk. All five cover
-- temporal-suspend-partial-failure: Sectors sits at position 3 by construction (it sorts after
-- "Regions" and before "Teams" -- verify this yourself rather than trust the comment if the
-- suspend loop's ordering ever changes), so a deterministic failure suspending Sectors leaves
-- pairs 1-2 already-suspended-and-stranded and pairs 4-5 never-reached on either side.
CREATE TABLE dbo.Districts
(
    DistrictId   INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Districts PRIMARY KEY CLUSTERED,
    DistrictName NVARCHAR (100) NOT NULL,
    ValidFrom    DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo      DATETIME2 (7) GENERATED ALWAYS AS ROW END   NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.DistrictHistory));

CREATE TABLE dbo.Regions
(
    RegionId   INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Regions PRIMARY KEY CLUSTERED,
    RegionName NVARCHAR (100) NOT NULL,
    ValidFrom  DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo    DATETIME2 (7) GENERATED ALWAYS AS ROW END   NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.RegionHistory));

CREATE TABLE dbo.Sectors
(
    SectorId   INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Sectors PRIMARY KEY CLUSTERED,
    SectorName NVARCHAR (100) NOT NULL,
    ValidFrom  DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo    DATETIME2 (7) GENERATED ALWAYS AS ROW END   NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.SectorHistory));

CREATE TABLE dbo.Teams
(
    TeamId    INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Teams PRIMARY KEY CLUSTERED,
    TeamName  NVARCHAR (100) NOT NULL,
    ValidFrom DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo   DATETIME2 (7) GENERATED ALWAYS AS ROW END   NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.TeamHistory));

CREATE TABLE dbo.Territories
(
    TerritoryId   INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Territories PRIMARY KEY CLUSTERED,
    TerritoryName NVARCHAR (100) NOT NULL,
    ValidFrom     DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo       DATETIME2 (7) GENERATED ALWAYS AS ROW END   NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.TerritoryHistory));

-- Deterministic un-suspendable pair for temporal-suspend-partial-failure: a login that is a
-- db_owner member (so it can do everything else the import needs, including suspending the
-- other four pairs) but has ALTER explicitly denied on dbo.Sectors. DENY set directly on a user
-- overrides permissions the user gets through role membership, so this fails ALTER TABLE
-- dbo.Sectors ... SET (SYSTEM_VERSIONING = OFF) -- the first statement in the suspend ceremony --
-- deterministically, with a real permission error, no lock timeout and no reliance on the test
-- connecting as anything other than this login. The partial-failure test must import over a
-- connection string authenticating as this login rather than sa. CHECK_POLICY = OFF because SQL
-- Server on Linux (the usual test-container image) can't evaluate the Windows password policy.
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'TemporalSuite_PartialFailureImporter')
BEGIN
    CREATE LOGIN TemporalSuite_PartialFailureImporter
        WITH PASSWORD = N'P@rt1alFa!lure_2026', CHECK_POLICY = OFF, CHECK_EXPIRATION = OFF;
END;

CREATE USER TemporalSuite_PartialFailureImporter FOR LOGIN TemporalSuite_PartialFailureImporter;
ALTER ROLE db_owner ADD MEMBER TemporalSuite_PartialFailureImporter;
DENY ALTER ON OBJECT::dbo.Sectors TO TemporalSuite_PartialFailureImporter;

-- Sixth, deliberately empty pair for temporal-restore-failure-handling: the test suspends it,
-- drops the period, then drops ValidTo to make restore impossible. Kept separate from the five
-- pairs above so that destructive test never touches data another test depends on.
CREATE TABLE dbo.Outposts
(
    OutpostId   INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_Outposts PRIMARY KEY CLUSTERED,
    OutpostName NVARCHAR (100) NOT NULL,
    ValidFrom   DATETIME2 (7) GENERATED ALWAYS AS ROW START NOT NULL,
    ValidTo     DATETIME2 (7) GENERATED ALWAYS AS ROW END   NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.OutpostHistory));

-- @@SEED
-- WAITFOR between insert and update forces a real clock tick so the closed row versions get a
-- non-zero period; SQL Server discards zero-duration history rows.
INSERT INTO dbo.Departments (DepartmentName, ManagerId)
VALUES (N'Sales', 10),
       (N'Engineering', 20),
       (N'Support', 30);
WAITFOR
DELAY '00:00:00.050';
UPDATE dbo.Departments
SET ManagerId = 11
WHERE DepartmentName = N'Sales';
UPDATE dbo.Departments
SET ManagerId = 21
WHERE DepartmentName = N'Engineering';
DELETE FROM dbo.Departments
WHERE DepartmentName = N'Support';

INSERT INTO dbo.Offices (OfficeName)
VALUES (N'HQ'),
       (N'Branch');
INSERT INTO dbo.Workers (OfficeId, WorkerName)
VALUES (1, N'Ann'),
       (2, N'Bob');
WAITFOR
DELAY '00:00:00.050';
UPDATE dbo.Workers
SET WorkerName = N'Ann-2'
WHERE OfficeId = 1;

INSERT INTO dbo.Flags (FlagName)
VALUES (N'x'),
       (N'y');
WAITFOR
DELAY '00:00:00.050';
UPDATE dbo.Flags
SET FlagName = N'x2'
WHERE FlagName = N'x';

INSERT INTO dbo.Subscriptions (CustomerId, PlanName)
VALUES (1, N'Basic'),
       (2, N'Pro');
WAITFOR
DELAY '00:00:00.050';
UPDATE dbo.Subscriptions
SET PlanName = N'Premium'
WHERE CustomerId = 1;

INSERT INTO dbo.Ledgers (Note)
VALUES (N'one'),
       (N'two'),
       (N'three');

INSERT INTO dbo.Districts (DistrictName)
VALUES (N'Central');
WAITFOR
DELAY '00:00:00.050';
UPDATE dbo.Districts
SET DistrictName = N'Central-2'
WHERE DistrictName = N'Central';

INSERT INTO dbo.Regions (RegionName)
VALUES (N'North'),
       (N'South');
WAITFOR
DELAY '00:00:00.050';
UPDATE dbo.Regions
SET RegionName = N'North-2'
WHERE RegionName = N'North';

INSERT INTO dbo.Sectors (SectorName)
VALUES (N'Sigma');
WAITFOR
DELAY '00:00:00.050';
UPDATE dbo.Sectors
SET SectorName = N'Sigma-2'
WHERE SectorName = N'Sigma';

INSERT INTO dbo.Teams (TeamName)
VALUES (N'Alpha');
WAITFOR
DELAY '00:00:00.050';
UPDATE dbo.Teams
SET TeamName = N'Alpha-2'
WHERE TeamName = N'Alpha';

INSERT INTO dbo.Territories (TerritoryName)
VALUES (N'Frontier');
WAITFOR
DELAY '00:00:00.050';
UPDATE dbo.Territories
SET TerritoryName = N'Frontier-2'
WHERE TerritoryName = N'Frontier';

-- dbo.Outposts is intentionally left empty.
