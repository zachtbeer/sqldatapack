-- A legacy-staging-shaped table spanning every ordinary SQL Server scalar type.
CREATE TABLE dbo.LegacyImportRows
(
    LegacyImportRowId   INT IDENTITY(1,1) PRIMARY KEY,
    TinyValue            TINYINT           NOT NULL,
    SmallValue           SMALLINT          NOT NULL,
    IntValue             INT               NOT NULL,
    BigValue              BIGINT            NOT NULL,
    RealValue              REAL              NOT NULL,
    FloatValue              FLOAT             NOT NULL,
    NumericValue             NUMERIC(12, 4)    NOT NULL,
    DecimalValue              DECIMAL(18, 6)    NOT NULL,
    DecimalTight               DECIMAL(5, 5)     NOT NULL,  -- max scale: every digit is after the point
    DecimalHighPrecision         DECIMAL(28, 10)   NOT NULL,  -- high precision, still inside .NET decimal range
    MoneyValue                    MONEY             NOT NULL,
    SmallMoneyValue                 SMALLMONEY        NOT NULL,
    DateValue                        DATE              NOT NULL,
    DateTimeValue                      DATETIME          NOT NULL,
    DateTime2Value                       DATETIME2(7)      NOT NULL,
    DateTimeOffsetValue                    DATETIMEOFFSET(3) NOT NULL,
    TimeValue                                TIME(4)           NOT NULL,
    GuidValue                                  UNIQUEIDENTIFIER  NULL,
    FlagValue                                    BIT               NULL,
    BlobValue                                      VARBINARY(MAX)    NULL,
    BigTextValue                                     NVARCHAR(MAX)     NULL,
    NullableText                                     NVARCHAR(50)      NULL,
    NullableInt                                        INT               NULL,
    NullableDate                                         DATETIME2(3)      NULL
);

-- decimal(38,x) beyond .NET decimal's ~29 total-significant-digit ceiling. Row-count is 1: the
-- point is that export fails naming this table, these columns, and the offending values.
CREATE TABLE dbo.LedgerAmounts
(
    LedgerAmountId   INT IDENTITY(1,1) PRIMARY KEY,
    Description       NVARCHAR(100) NOT NULL,
    HugeWholeAmount     DECIMAL(38, 0)  NOT NULL,
    HugeScaledAmount      DECIMAL(38, 10) NOT NULL
);

-- Every DATETIME2 fractional precision plus DATE, SMALLDATETIME, DATETIME, TIME(7) and
-- DATETIMEOFFSET(7) at both offset extremes, in one wide row shape.
-- NOTE: datetimeoffset validates both the local AND the UTC-normalized value on insert, so an
-- offset extreme can only be paired with a date extreme when the UTC value also stays in range.
-- Year 0001 can only take -14:00 or a smaller positive offset (not +14:00, whose UTC is year 0000).
-- Year 9999 can only take +14:00 or a smaller negative offset (not -14:00, whose UTC is year 10000).
-- Both offset extremes (+14:00 and -14:00) are still exercised together, just on the mid-range row.
CREATE TABLE dbo.ChronoExtremes
(
    ChronoExtremeId INT IDENTITY(1,1) PRIMARY KEY,
    Dt2Precision0   DATETIME2(0) NOT NULL,
    Dt2Precision1   DATETIME2(1) NOT NULL,
    Dt2Precision2   DATETIME2(2) NOT NULL,
    Dt2Precision3   DATETIME2(3) NOT NULL,
    Dt2Precision4   DATETIME2(4) NOT NULL,
    Dt2Precision5   DATETIME2(5) NOT NULL,
    Dt2Precision6   DATETIME2(6) NOT NULL,
    Dt2Precision7   DATETIME2(7) NOT NULL,
    DateOnly        DATE NOT NULL,
    SmallDt         SMALLDATETIME NOT NULL,
    RegularDt       DATETIME NOT NULL,
    TimeOfDay       TIME(7) NOT NULL,
    OffsetHigh      DATETIMEOFFSET(7) NOT NULL,
    OffsetLow       DATETIMEOFFSET(7) NOT NULL
);

-- Pairs with narrow-target-variants.dbo.DriftTarget for type-drift-not-validated.
CREATE TABLE dbo.DriftSamples
(
    DriftSampleId INT IDENTITY(1,1) PRIMARY KEY,
    RecordedAt    DATETIME2(7)   NOT NULL,
    Description   NVARCHAR(100)  NOT NULL,
    Amount        DECIMAL(18, 6) NOT NULL
);

-- Pairs with narrow-target-variants.dbo.FixedWidthTextsSwapped for fixed-width-and-collation-semantics.
CREATE TABLE dbo.FixedWidthTexts
(
    FixedWidthTextId INT IDENTITY(1,1) PRIMARY KEY,
    CodeChar          CHAR(10)     NOT NULL,
    CodeNChar         NCHAR(10)    NOT NULL,
    LabelVarchar      VARCHAR(50)  NOT NULL,
    LabelNVarchar     NVARCHAR(50) COLLATE Latin1_General_CI_AI NOT NULL
);

-- Single NOT NULL nvarchar column; each row is a different Unicode hazard. Values are written
-- with NCHAR(...) so the file itself stays ASCII regardless of editor encoding.
CREATE TABLE dbo.UnicodeHazards
(
    UnicodeHazardId INT IDENTITY(1,1) PRIMARY KEY,
    HazardText      NVARCHAR(200) NOT NULL
);

CREATE TABLE dbo.DocumentPayloads
(
    DocumentPayloadId INT IDENTITY(1,1) PRIMARY KEY,
    PayloadName        NVARCHAR(40) NOT NULL,
    PayloadXml         XML NULL
);

-- Native JSON is a SQL Server 2025+ type; add it only when the engine actually has it so this
-- script still deploys cleanly on 2022.
IF CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) >= 17
    EXEC('ALTER TABLE dbo.DocumentPayloads ADD PayloadJson JSON NULL');

-- Base table has no vector columns so CREATE TABLE always succeeds, including on 2022.
CREATE TABLE dbo.VectorSamples
(
    VectorSampleId INT IDENTITY(1,1) PRIMARY KEY,
    Label          NVARCHAR(40) NOT NULL
);

-- VECTOR is a SQL Server 2025+ type, same as JSON above -- add it only when the engine has it.
-- Un-gating this (as the earlier draft did) took the whole database down on a 2022 image.
IF CAST(SERVERPROPERTY('ProductMajorVersion') AS INT) >= 17
    EXEC('ALTER TABLE dbo.VectorSamples ADD Embedding VECTOR(3) NULL');

-- float16 vectors are an opt-in preview feature; no-op quietly if the engine/edition lacks them.
-- The two statements need separate batches: turning preview features on does not affect a
-- VECTOR(n, float16) in the same batch, which then fails with "'float16' is not a recognized vector
-- base type" and gets swallowed, leaving the column silently missing.
BEGIN TRY
    EXEC('ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON');
END TRY
BEGIN CATCH
    -- Engine predates PREVIEW_FEATURES; the float32 column above still stands.
END CATCH;
GO

BEGIN TRY
    EXEC('ALTER TABLE dbo.VectorSamples ADD EmbeddingFloat16 VECTOR(3, float16) NULL');
END TRY
BEGIN CATCH
    -- Engine predates float16 vectors; the float32 column above still stands.
END CATCH;

-- Alias type over a base type. Each unsupported-type hazard below gets its own table with exactly
-- one hazard column and no rows, so planning fails before any row is read and the export error
-- must name that table's own column and type -- no hazard can hide behind another one erroring first.
CREATE TYPE dbo.PhoneNumber FROM VARCHAR(20) NULL;
-- Type names get no deferred name resolution, so the CREATE TABLE below needs its own batch.
GO

CREATE TABLE dbo.AliasTypeHazard
(
    AliasTypeHazardId INT IDENTITY(1,1) PRIMARY KEY,
    Phone              dbo.PhoneNumber NULL
);

CREATE TABLE dbo.SysnameHazard
(
    SysnameHazardId INT IDENTITY(1,1) PRIMARY KEY,
    CatalogName      SYSNAME NULL
);

CREATE TABLE dbo.SpatialHazard
(
    SpatialHazardId INT IDENTITY(1,1) PRIMARY KEY,
    Location         GEOGRAPHY NULL,
    Shape            GEOMETRY NULL
);

CREATE TABLE dbo.HierarchyHazard
(
    HierarchyHazardId INT IDENTITY(1,1) PRIMARY KEY,
    OrgNode            HIERARCHYID NULL
);

CREATE TABLE dbo.VariantHazard
(
    VariantHazardId INT IDENTITY(1,1) PRIMARY KEY,
    LegacyValue      SQL_VARIANT NULL
);


-- @@SEED

INSERT INTO dbo.LegacyImportRows (
    TinyValue, SmallValue, IntValue, BigValue, RealValue, FloatValue, NumericValue, DecimalValue,
    DecimalTight, DecimalHighPrecision, MoneyValue, SmallMoneyValue, DateValue, DateTimeValue,
    DateTime2Value, DateTimeOffsetValue, TimeValue, GuidValue, FlagValue, BlobValue, BigTextValue,
    NullableText, NullableInt, NullableDate
) VALUES
(
    -- Row 1: hostile float/money boundary values. 0.1 and the MONEY/SMALLMONEY max both need
    -- every significant digit to round-trip; a "G"-vs-"R" formatting bug only shows up here.
    250, -1234, 123456, 9876543210, CAST(0.1 AS REAL), 0.1,
    CAST(12345.6789 AS NUMERIC(12, 4)), CAST(987654321.123456 AS DECIMAL(18, 6)),
    CAST(0.99999 AS DECIMAL(5, 5)), CAST(123456789012345678.9876543210 AS DECIMAL(28, 10)),
    CAST(922337203685477.5807 AS MONEY), CAST(214748.3647 AS SMALLMONEY),
    CAST('2024-03-04' AS DATE), CAST('2024-03-04T05:06:07.123' AS DATETIME),
    CAST('2024-03-04T05:06:07.1234567' AS DATETIME2(7)),
    CAST('2024-03-04T05:06:07.890-07:00' AS DATETIMEOFFSET(3)),
    CAST('12:34:56.7891' AS TIME(4)),
    CAST('6f9619ff-8b86-d011-b42d-00c04fc964ff' AS UNIQUEIDENTIFIER),
    1, 0x01020304,
    -- ~4KB of mixed-script repeated text, to exercise NVARCHAR(MAX) (max_length = -1 in sys.columns)
    REPLICATE(NCHAR(0x4E2D) + NCHAR(0x6587) + N' mixed ' + NCHAR(0x00E9) + N'dition text block ', 200),
    N'nullable text', 42, CAST('2024-04-05T06:07:08.123' AS DATETIME2(3))
),
(
    -- Row 2: the opposite float/money boundary -- largest FLOAT magnitude, negative money extremes.
    1, 2, 3, 4, CAST(3.40282347E+38 AS REAL), 1.7976931348623157E+308,
    CAST(7.8901 AS NUMERIC(12, 4)), CAST(8.900000 AS DECIMAL(18, 6)),
    CAST(0.00001 AS DECIMAL(5, 5)), CAST(-123456789012345678.9876543210 AS DECIMAL(28, 10)),
    CAST(-922337203685477.5808 AS MONEY), CAST(-214748.3648 AS SMALLMONEY),
    CAST('2025-01-02' AS DATE), CAST('2025-01-02T03:04:05.127' AS DATETIME),
    CAST('2025-01-02T03:04:05.9876543' AS DATETIME2(7)),
    CAST('2025-01-02T03:04:05.432+02:30' AS DATETIMEOFFSET(3)),
    CAST('01:02:03.4567' AS TIME(4)),
    CAST('aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee' AS UNIQUEIDENTIFIER),
    0, 0x0A0B0C0D, NULL,
    NULL, NULL, NULL
),
(
    -- Row 3: the boring control row -- also carries -0.0 (sign bit only survives with round-trippable
    -- formatting) and is the NULL row for bit/guid/binary/max-text (closes the NULL coverage gap).
    0, 0, 0, 0, CAST(0 AS REAL), CAST(-0.0 AS FLOAT),
    CAST(0 AS NUMERIC(12, 4)), CAST(0 AS DECIMAL(18, 6)),
    CAST(0 AS DECIMAL(5, 5)), CAST(0 AS DECIMAL(28, 10)),
    CAST(0 AS MONEY), CAST(0 AS SMALLMONEY),
    CAST('1753-01-01' AS DATE), CAST('1753-01-01T00:00:00.000' AS DATETIME),
    CAST('0001-01-01T00:00:00.0000000' AS DATETIME2(7)),
    CAST('0001-01-01T00:00:00.0000000+00:00' AS DATETIMEOFFSET(3)),
    CAST('00:00:00.0000' AS TIME(4)),
    NULL, NULL, NULL, NULL,
    NULL, NULL, NULL
);

INSERT INTO dbo.LedgerAmounts (Description, HugeWholeAmount, HugeScaledAmount) VALUES
    (N'beyond .NET decimal range', 99999999999999999999999999999999999999, 9999999999999999999999999999.9999999999);

INSERT INTO dbo.ChronoExtremes (
    Dt2Precision0, Dt2Precision1, Dt2Precision2, Dt2Precision3, Dt2Precision4, Dt2Precision5,
    Dt2Precision6, Dt2Precision7, DateOnly, SmallDt, RegularDt, TimeOfDay, OffsetHigh, OffsetLow
) VALUES
(
    -- Year 0001: UTC must also stay >= 0001-01-01, so the offsets here are -14:00 and +00:00,
    -- not +14:00 (which would push UTC into year 0000 and SQL Server rejects the insert outright).
    CAST('0001-01-01T00:00:00' AS DATETIME2(0)), CAST('0001-01-01T00:00:00.0' AS DATETIME2(1)),
    CAST('0001-01-01T00:00:00.00' AS DATETIME2(2)), CAST('0001-01-01T00:00:00.000' AS DATETIME2(3)),
    CAST('0001-01-01T00:00:00.0000' AS DATETIME2(4)), CAST('0001-01-01T00:00:00.00000' AS DATETIME2(5)),
    CAST('0001-01-01T00:00:00.000000' AS DATETIME2(6)), CAST('0001-01-01T00:00:00.0000000' AS DATETIME2(7)),
    CAST('0001-01-01' AS DATE), CAST('1900-01-01T00:00:00' AS SMALLDATETIME),
    CAST('1753-01-01T00:00:00.000' AS DATETIME), CAST('00:00:00.0000000' AS TIME(7)),
    CAST('0001-01-01T00:00:00.0000000-14:00' AS DATETIMEOFFSET(7)),
    CAST('0001-01-01T00:00:00.0000000+00:00' AS DATETIMEOFFSET(7))
),
(
    -- Year 9999: UTC must stay <= 9999-12-31, so the offsets here are +14:00 and +12:00,
    -- not -14:00 (which would push UTC into year 10000 and SQL Server rejects the insert outright).
    CAST('9999-12-31T23:59:59' AS DATETIME2(0)), CAST('9999-12-31T23:59:59.9' AS DATETIME2(1)),
    CAST('9999-12-31T23:59:59.99' AS DATETIME2(2)), CAST('9999-12-31T23:59:59.999' AS DATETIME2(3)),
    CAST('9999-12-31T23:59:59.9999' AS DATETIME2(4)), CAST('9999-12-31T23:59:59.99999' AS DATETIME2(5)),
    CAST('9999-12-31T23:59:59.999999' AS DATETIME2(6)), CAST('9999-12-31T23:59:59.9999999' AS DATETIME2(7)),
    CAST('9999-12-31' AS DATE), CAST('2079-06-06T23:59:00' AS SMALLDATETIME),
    CAST('9999-12-31T23:59:59.997' AS DATETIME), CAST('23:59:59.9999999' AS TIME(7)),
    CAST('9999-12-31T23:59:59.9999999+14:00' AS DATETIMEOFFSET(7)),
    CAST('9999-12-31T23:59:59.9999999+12:00' AS DATETIMEOFFSET(7))
),
(
    -- Mid-range 2024 row: both offset extremes together, since UTC is nowhere near either boundary here.
    CAST('2024-06-15T12:30:45' AS DATETIME2(0)), CAST('2024-06-15T12:30:45.1' AS DATETIME2(1)),
    CAST('2024-06-15T12:30:45.12' AS DATETIME2(2)), CAST('2024-06-15T12:30:45.123' AS DATETIME2(3)),
    CAST('2024-06-15T12:30:45.1234' AS DATETIME2(4)), CAST('2024-06-15T12:30:45.12345' AS DATETIME2(5)),
    CAST('2024-06-15T12:30:45.123456' AS DATETIME2(6)), CAST('2024-06-15T12:30:45.1234567' AS DATETIME2(7)),
    CAST('2024-06-15' AS DATE), CAST('2024-06-15T12:31:00' AS SMALLDATETIME),
    CAST('2024-06-15T12:30:45.123' AS DATETIME), CAST('12:30:45.1234567' AS TIME(7)),
    CAST('2024-06-15T12:30:45.1234567+14:00' AS DATETIMEOFFSET(7)),
    CAST('2024-06-15T12:30:45.1234567-14:00' AS DATETIMEOFFSET(7))
);

INSERT INTO dbo.DriftSamples (RecordedAt, Description, Amount) VALUES
    (CAST('2024-05-01T10:11:12.1234567' AS DATETIME2(7)), NCHAR(0x4E2D) + NCHAR(0x6587) + N' report ' + NCHAR(0xE9) + N'dition', CAST(1234.567891 AS DECIMAL(18, 6))),
    (CAST('2024-05-02T08:09:10.7654321' AS DATETIME2(7)), N'plain ascii description', CAST(99.999999 AS DECIMAL(18, 6)));

INSERT INTO dbo.FixedWidthTexts (CodeChar, CodeNChar, LabelVarchar, LabelNVarchar) VALUES
    ('ABC', N'XYZ', 'trailing space   ', NCHAR(0xE9) + N'clat ' + NCHAR(0x4E2D) + NCHAR(0x6587)),
    ('', N'', '', N''),
    ('Z', N'W', 'plain', N'plain ascii label');

INSERT INTO dbo.UnicodeHazards (HazardText) VALUES
    -- Emoji ZWJ family sequence: man + ZWJ + woman + ZWJ + girl + ZWJ + boy
    (NCHAR(0xD83D) + NCHAR(0xDC68) + NCHAR(0x200D) + NCHAR(0xD83D) + NCHAR(0xDC69) + NCHAR(0x200D) + NCHAR(0xD83D) + NCHAR(0xDC67) + NCHAR(0x200D) + NCHAR(0xD83D) + NCHAR(0xDC66)),
    -- RTL: Arabic + Hebrew + Latin
    (NCHAR(0x0645) + NCHAR(0x0631) + NCHAR(0x062D) + NCHAR(0x0628) + NCHAR(0x0627) + N' ' + NCHAR(0x05E9) + NCHAR(0x05DC) + NCHAR(0x05D5) + NCHAR(0x05DD) + N' world'),
    -- CJK Han + Hiragana + Katakana
    (NCHAR(0x4E2D) + NCHAR(0x6587) + N' / ' + NCHAR(0x65E5) + NCHAR(0x672C) + NCHAR(0x8A9E)),
    -- Combining diacritic vs its precomposed form
    (N'a' + NCHAR(0x0301) + N' vs ' + NCHAR(0x00E1)),
    -- Zero-width joiner between two plain letters
    (N'A' + NCHAR(0x200D) + N'B'),
    -- Supplementary-plane codepoint: musical G clef U+1D11E as a surrogate pair
    (NCHAR(0xD834) + NCHAR(0xDD1E) + N' G clef');

INSERT INTO dbo.DocumentPayloads (PayloadName, PayloadXml) VALUES
    (N'element-attribute', CAST(N'<root><item id="1">alpha</item></root>' AS XML)),
    (N'namespaced', CAST(N'<ns:root xmlns:ns="urn:test"><ns:item name="beta">value</ns:item></ns:root>' AS XML)),
    (N'mixed-content', CAST(N'<root>leading <b>bold</b> trailing</root>' AS XML)),
    (N'null-payload', NULL);

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.DocumentPayloads') AND name = 'PayloadJson')
BEGIN
    EXEC('UPDATE dbo.DocumentPayloads SET PayloadJson = ''{"id":1,"tags":["one","two"]}'' WHERE PayloadName = ''element-attribute''');
    EXEC('UPDATE dbo.DocumentPayloads SET PayloadJson = ''{"id":2,"profile":{"active":true,"score":12.5}}'' WHERE PayloadName = ''namespaced''');
    EXEC('UPDATE dbo.DocumentPayloads SET PayloadJson = NULL WHERE PayloadName = ''null-payload''');
END;

INSERT INTO dbo.VectorSamples (Label) VALUES
    (N'unit'), (N'triple'), (N'frac'), (N'null-vector');

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.VectorSamples') AND name = 'Embedding')
BEGIN
    EXEC('UPDATE dbo.VectorSamples SET Embedding = ''[1,0,0]'' WHERE Label = ''unit''');
    EXEC('UPDATE dbo.VectorSamples SET Embedding = ''[2,-3,4]'' WHERE Label = ''triple''');
    EXEC('UPDATE dbo.VectorSamples SET Embedding = ''[0.5,0.25,-0.125]'' WHERE Label = ''frac''');
    EXEC('UPDATE dbo.VectorSamples SET Embedding = NULL WHERE Label = ''null-vector''');
END;

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.VectorSamples') AND name = 'EmbeddingFloat16')
BEGIN
    EXEC('UPDATE dbo.VectorSamples SET EmbeddingFloat16 = ''[1,0,0]'' WHERE Label = ''unit''');
    EXEC('UPDATE dbo.VectorSamples SET EmbeddingFloat16 = ''[2,-3,4]'' WHERE Label = ''triple''');
    EXEC('UPDATE dbo.VectorSamples SET EmbeddingFloat16 = NULL WHERE Label = ''null-vector''');
END;

-- No CAST: SQL Server has no cast to an alias type, only implicit assignment to a column of one.
INSERT INTO dbo.AliasTypeHazard (Phone) VALUES ('+1-555-0100');

INSERT INTO dbo.SysnameHazard (CatalogName) VALUES (N'dbo.SysnameHazard');

INSERT INTO dbo.SpatialHazard (Location, Shape) VALUES
    (geography::Point(52.379189, 4.899431, 4326), geometry::STGeomFromText('LINESTRING(0 0, 1 1, 2 2)', 0));

INSERT INTO dbo.HierarchyHazard (OrgNode) VALUES (hierarchyid::GetRoot());

INSERT INTO dbo.VariantHazard (LegacyValue) VALUES (CAST(N'legacy-note' AS SQL_VARIANT));
