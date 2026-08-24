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
    RegionLookupId INT IDENTITY(1,1) PRIMARY KEY,
    RegionCode     NVARCHAR(10) NOT NULL
);

-- CREATE FUNCTION must be the only statement in its batch; the whole file runs as one
-- batch, so push it into its own batch via dynamic SQL.
EXEC(N'
CREATE FUNCTION dbo.fn_ComputeExtendedPrice(@qty INT, @unitPrice DECIMAL(18, 2))
RETURNS DECIMAL(18, 2)
AS
BEGIN
    RETURN @qty * @unitPrice;
END;
');

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
    CONSTRAINT FK_SelectedChild_SelectedParent FOREIGN KEY (ParentId) REFERENCES dbo.SelectedParent (ParentId),
    CONSTRAINT FK_SelectedChild_RegionLookup FOREIGN KEY (RegionLookupId) REFERENCES dbo.RegionLookup (RegionLookupId)
);

-- Extra index absent from the package's model.
CREATE INDEX IX_SelectedChild_RegionLookupId ON dbo.SelectedChild (RegionLookupId);

CREATE TABLE dbo.SelectedChildAudit
(
    SelectedChildAuditId INT IDENTITY(1,1) PRIMARY KEY,
    ChildId              INT NOT NULL,
    ChangedAt            DATETIME2(3) NOT NULL CONSTRAINT DF_SelectedChildAudit_ChangedAt DEFAULT SYSUTCDATETIME()
);

-- Extra trigger absent from the package's model. Same batch problem as the function above:
-- CREATE TRIGGER has to be first in its batch, so it goes through EXEC() too.
EXEC(N'
CREATE TRIGGER trg_SelectedChild_Audit ON dbo.SelectedChild
AFTER INSERT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO dbo.SelectedChildAudit (ChildId) SELECT ChildId FROM inserted;
END;
');

-- Wholly unrelated table: not in the package at all, so only a database-scope deploy with
-- AllowObjectDrops = true can remove it; a selected-table-scope deploy never touches it either way.
CREATE TABLE dbo.LegacyStagingImport
(
    LegacyStagingImportId INT IDENTITY(1,1) PRIMARY KEY,
    RawPayload             NVARCHAR(200) NOT NULL
);

INSERT INTO dbo.SelectedParent (ParentCode, ParentName) VALUES (N'P1', N'Parent One');
INSERT INTO dbo.RegionLookup (RegionCode) VALUES (N'EU'), (N'US');
INSERT INTO dbo.SelectedChild (ParentId, Qty, UnitPrice, RegionLookupId) VALUES (1, 2, 12.50, 1);
INSERT INTO dbo.LegacyStagingImport (RawPayload) VALUES (N'pre-existing, unrelated to the package');
