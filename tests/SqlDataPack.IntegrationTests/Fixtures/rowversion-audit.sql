CREATE TABLE dbo.AuditTrails
(
    AuditTrailId INT IDENTITY(1,1) PRIMARY KEY,
    EventName    NVARCHAR(50) NOT NULL,
    Rv           ROWVERSION
);

-- Same shape, no rowversion column: the source half of "source without one, target with one".
CREATE TABLE dbo.AuditTrailsLegacy
(
    AuditTrailLegacyId INT IDENTITY(1,1) PRIMARY KEY,
    EventName           NVARCHAR(50) NOT NULL
);

-- @@SEED

INSERT INTO dbo.AuditTrails (EventName)
VALUES (N'login'),
       (N'logout'),
       (N'password-reset');

INSERT INTO dbo.AuditTrailsLegacy (EventName)
VALUES (N'login'),
       (N'logout'),
       (N'password-reset');
