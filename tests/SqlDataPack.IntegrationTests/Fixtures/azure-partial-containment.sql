-- CONTAINMENT is a database-level setting, so this touches the current database directly rather
-- than staying inside a schema. Requires sysadmin (typical for an integration-test container).
EXEC sp_configure 'contained database authentication', 1;
RECONFIGURE;

-- EXEC() cannot concatenate a function call, so the statement is built into a variable first.
DECLARE @sql nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(DB_NAME()) + N' SET CONTAINMENT = PARTIAL WITH ROLLBACK IMMEDIATE';
EXEC (@sql);

CREATE TABLE dbo.RemoteOffices
(
    RemoteOfficeId INT IDENTITY (1, 1) NOT NULL CONSTRAINT PK_RemoteOffices PRIMARY KEY CLUSTERED,
    OfficeName     NVARCHAR (100) NOT NULL,
    TimeZoneName   NVARCHAR (50) NOT NULL
);

-- A contained database user is what actually requires containment; without one, PARTIAL
-- containment is set but never exercised.
CREATE USER ContainedReportingUser WITH PASSWORD = 'Str0ng!ContainedPwd#2026', DEFAULT_SCHEMA = dbo;
GRANT SELECT ON dbo.RemoteOffices TO ContainedReportingUser;

INSERT INTO dbo.RemoteOffices (OfficeName, TimeZoneName)
VALUES (N'Amsterdam HQ', N'Europe/Amsterdam'),
       (N'Austin Satellite', N'America/Chicago');
