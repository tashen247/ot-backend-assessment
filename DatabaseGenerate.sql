/* =====================================================================================
   OT_Assessment_DB - schema generation script
   -------------------------------------------------------------------------------------
   Safe to re-run: every object is created only if it does not already exist, and
   stored procedures use CREATE OR ALTER.

   Design summary (see Notes.md for the reasoning):
     dbo.Provider       - game providers, one row per provider name
     dbo.Game           - games, unique per (ProviderId, Name)
     dbo.Player         - players keyed by AccountId, plus a running spend/wager total
                          maintained in the same transaction as the wager insert
     dbo.CasinoWager    - one row per wager, natural key WagerId (idempotent writes)

     dbo.CasinoWagerTableType            - TVP used by the consumer to write a batch
     dbo.usp_CasinoWager_InsertBatch     - idempotent batch insert + dimension upsert
     dbo.usp_CasinoWager_GetByPlayer     - paged wagers for a player, latest first
     dbo.usp_Player_GetTopSpenders       - top N players by total amount spent
   ===================================================================================== */

IF DB_ID(N'OT_Assessment_DB') IS NULL
BEGIN
    CREATE DATABASE OT_Assessment_DB;
END
GO

/* Readers must never be blocked by the consumer's batch inserts. Row versioning lets the
   GET endpoints read the last committed version while a batch transaction is open. */
ALTER DATABASE OT_Assessment_DB SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
GO

USE OT_Assessment_DB;
GO

/* ------------------------------------------------------------------------------------
   Tables
   ------------------------------------------------------------------------------------ */

IF OBJECT_ID(N'dbo.Provider', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Provider
    (
        ProviderId      INT            IDENTITY(1,1) NOT NULL,
        Name            NVARCHAR(200)  NOT NULL,
        CreatedAtUtc    DATETIME2(3)   NOT NULL CONSTRAINT DF_Provider_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Provider PRIMARY KEY CLUSTERED (ProviderId),
        CONSTRAINT UQ_Provider_Name UNIQUE (Name)
    );
END
GO

IF OBJECT_ID(N'dbo.Game', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Game
    (
        GameId          INT            IDENTITY(1,1) NOT NULL,
        ProviderId      INT            NOT NULL,
        Name            NVARCHAR(200)  NOT NULL,
        Theme           NVARCHAR(50)   NULL,
        CreatedAtUtc    DATETIME2(3)   NOT NULL CONSTRAINT DF_Game_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Game PRIMARY KEY CLUSTERED (GameId),
        CONSTRAINT FK_Game_Provider FOREIGN KEY (ProviderId) REFERENCES dbo.Provider (ProviderId),
        -- Game names are only unique within a provider.
        CONSTRAINT UQ_Game_ProviderId_Name UNIQUE (ProviderId, Name)
    );
END
GO

IF OBJECT_ID(N'dbo.Player', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Player
    (
        PlayerId          INT               IDENTITY(1,1) NOT NULL,
        AccountId         UNIQUEIDENTIFIER  NOT NULL,
        Username          NVARCHAR(100)     NOT NULL,
        -- Running aggregates, maintained by usp_CasinoWager_InsertBatch in the same
        -- transaction as the wager rows, so they can never drift from dbo.CasinoWager.
        TotalAmountSpent  DECIMAL(19,4)     NOT NULL CONSTRAINT DF_Player_TotalAmountSpent DEFAULT (0),
        TotalWagerCount   INT               NOT NULL CONSTRAINT DF_Player_TotalWagerCount  DEFAULT (0),
        CreatedAtUtc      DATETIME2(3)      NOT NULL CONSTRAINT DF_Player_CreatedAtUtc     DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_Player PRIMARY KEY CLUSTERED (PlayerId),
        CONSTRAINT UQ_Player_AccountId UNIQUE (AccountId)
    );

    -- Serves usp_Player_GetTopSpenders as an ordered index scan of the first N rows.
    CREATE NONCLUSTERED INDEX IX_Player_TotalAmountSpent
        ON dbo.Player (TotalAmountSpent DESC)
        INCLUDE (AccountId, Username);
END
GO

IF OBJECT_ID(N'dbo.CasinoWager', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CasinoWager
    (
        -- Narrow, ever-increasing clustered key: appends at the end of the B-tree instead
        -- of the page splits a random GUID clustered key causes under insert load.
        CasinoWagerId        BIGINT            IDENTITY(1,1) NOT NULL,
        WagerId              UNIQUEIDENTIFIER  NOT NULL,
        PlayerId             INT               NOT NULL,
        GameId               INT               NOT NULL,
        TransactionId        UNIQUEIDENTIFIER  NOT NULL,
        TransactionTypeId    UNIQUEIDENTIFIER  NOT NULL,
        BrandId              UNIQUEIDENTIFIER  NOT NULL,
        ExternalReferenceId  UNIQUEIDENTIFIER  NOT NULL,
        Amount               DECIMAL(19,4)     NOT NULL,
        NumberOfBets         INT               NOT NULL,
        CountryCode          CHAR(2)           NOT NULL,
        DurationMs           BIGINT            NOT NULL,
        CreatedDateTime      DATETIMEOFFSET(7) NOT NULL,
        ReceivedAtUtc        DATETIME2(3)      NOT NULL CONSTRAINT DF_CasinoWager_ReceivedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_CasinoWager PRIMARY KEY CLUSTERED (CasinoWagerId),
        -- Natural key: makes at-least-once delivery from the queue safe.
        CONSTRAINT UQ_CasinoWager_WagerId UNIQUE NONCLUSTERED (WagerId),
        CONSTRAINT FK_CasinoWager_Player FOREIGN KEY (PlayerId) REFERENCES dbo.Player (PlayerId),
        CONSTRAINT FK_CasinoWager_Game   FOREIGN KEY (GameId)   REFERENCES dbo.Game (GameId),
        CONSTRAINT CK_CasinoWager_Amount       CHECK (Amount >= 0),
        CONSTRAINT CK_CasinoWager_NumberOfBets CHECK (NumberOfBets > 0)
    );

    -- Covers usp_CasinoWager_GetByPlayer: seek on PlayerId, already in "latest first"
    -- order, no key lookups.
    CREATE NONCLUSTERED INDEX IX_CasinoWager_PlayerId_CreatedDateTime
        ON dbo.CasinoWager (PlayerId, CreatedDateTime DESC, CasinoWagerId DESC)
        INCLUDE (WagerId, GameId, Amount);

    -- Supports the FK and game-level reporting.
    CREATE NONCLUSTERED INDEX IX_CasinoWager_GameId
        ON dbo.CasinoWager (GameId);
END
GO

/* ------------------------------------------------------------------------------------
   Table type used to pass a batch of wagers in one round trip
   ------------------------------------------------------------------------------------ */

IF TYPE_ID(N'dbo.CasinoWagerTableType') IS NULL
BEGIN
    CREATE TYPE dbo.CasinoWagerTableType AS TABLE
    (
        WagerId              UNIQUEIDENTIFIER  NOT NULL,
        AccountId            UNIQUEIDENTIFIER  NOT NULL,
        Username             NVARCHAR(100)     NOT NULL,
        ProviderName         NVARCHAR(200)     NOT NULL,
        GameName             NVARCHAR(200)     NOT NULL,
        Theme                NVARCHAR(50)      NULL,
        TransactionId        UNIQUEIDENTIFIER  NOT NULL,
        TransactionTypeId    UNIQUEIDENTIFIER  NOT NULL,
        BrandId              UNIQUEIDENTIFIER  NOT NULL,
        ExternalReferenceId  UNIQUEIDENTIFIER  NOT NULL,
        Amount               DECIMAL(19,4)     NOT NULL,
        NumberOfBets         INT               NOT NULL,
        CountryCode          CHAR(2)           NOT NULL,
        DurationMs           BIGINT            NOT NULL,
        CreatedDateTime      DATETIMEOFFSET(7) NOT NULL
    );
END
GO

/* ------------------------------------------------------------------------------------
   Stored procedures
   ------------------------------------------------------------------------------------ */

CREATE OR ALTER PROCEDURE dbo.usp_CasinoWager_InsertBatch
    @Wagers dbo.CasinoWagerTableType READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    -- 1. De-duplicate within the batch (a redelivered message can land in the same batch).
    SELECT  w.WagerId, w.AccountId, w.Username, w.ProviderName, w.GameName, w.Theme,
            w.TransactionId, w.TransactionTypeId, w.BrandId, w.ExternalReferenceId,
            w.Amount, w.NumberOfBets, w.CountryCode, w.DurationMs, w.CreatedDateTime
    INTO    #Batch
    FROM (
        SELECT *, ROW_NUMBER() OVER (PARTITION BY WagerId ORDER BY (SELECT NULL)) AS RowNum
        FROM   @Wagers
    ) AS w
    WHERE w.RowNum = 1;

    BEGIN TRANSACTION;

    -- 2. Drop wagers already stored (redelivery after a crash between commit and ack).
    DELETE b
    FROM   #Batch AS b
    WHERE  EXISTS (SELECT 1 FROM dbo.CasinoWager AS cw WITH (UPDLOCK, HOLDLOCK)
                   WHERE cw.WagerId = b.WagerId);

    -- 3. Upsert dimensions. UPDLOCK + HOLDLOCK makes the existence check and insert atomic,
    --    so several consumer instances can run this procedure concurrently.
    INSERT INTO dbo.Provider (Name)
    SELECT DISTINCT b.ProviderName
    FROM   #Batch AS b
    WHERE  NOT EXISTS (SELECT 1 FROM dbo.Provider AS p WITH (UPDLOCK, HOLDLOCK)
                       WHERE p.Name = b.ProviderName);

    INSERT INTO dbo.Game (ProviderId, Name, Theme)
    SELECT p.ProviderId, b.GameName, MAX(b.Theme)
    FROM   #Batch AS b
           INNER JOIN dbo.Provider AS p ON p.Name = b.ProviderName
    WHERE  NOT EXISTS (SELECT 1 FROM dbo.Game AS g WITH (UPDLOCK, HOLDLOCK)
                       WHERE g.ProviderId = p.ProviderId AND g.Name = b.GameName)
    GROUP BY p.ProviderId, b.GameName;

    INSERT INTO dbo.Player (AccountId, Username)
    SELECT b.AccountId, MAX(b.Username)
    FROM   #Batch AS b
    WHERE  NOT EXISTS (SELECT 1 FROM dbo.Player AS pl WITH (UPDLOCK, HOLDLOCK)
                       WHERE pl.AccountId = b.AccountId)
    GROUP BY b.AccountId;

    -- 4. Insert the wagers against their surrogate keys. The rows actually written, not the
    --    rows offered, are what the totals and the returned count are based on.
    DECLARE @Inserted TABLE
    (
        WagerId UNIQUEIDENTIFIER NOT NULL,
        Amount  DECIMAL(19,4)    NOT NULL
    );

    INSERT INTO dbo.CasinoWager
        (WagerId, PlayerId, GameId, TransactionId, TransactionTypeId, BrandId, ExternalReferenceId,
         Amount, NumberOfBets, CountryCode, DurationMs, CreatedDateTime)
    OUTPUT inserted.WagerId, inserted.Amount INTO @Inserted (WagerId, Amount)
    SELECT b.WagerId, pl.PlayerId, g.GameId, b.TransactionId, b.TransactionTypeId, b.BrandId,
           b.ExternalReferenceId, b.Amount, b.NumberOfBets, b.CountryCode, b.DurationMs, b.CreatedDateTime
    FROM   #Batch AS b
           INNER JOIN dbo.Player   AS pl ON pl.AccountId = b.AccountId
           INNER JOIN dbo.Provider AS p  ON p.Name       = b.ProviderName
           INNER JOIN dbo.Game     AS g  ON g.ProviderId = p.ProviderId AND g.Name = b.GameName;

    -- A row still in #Batch that did not insert failed a join. Fail the transaction so the
    -- caller can write the batch one row at a time. Totals must not count a wager that was not stored.
    IF EXISTS (
        SELECT 1
        FROM   #Batch AS b
        WHERE  NOT EXISTS (SELECT 1 FROM @Inserted AS i WHERE i.WagerId = b.WagerId)
    )
    BEGIN
        THROW 50001, 'A wager in the batch could not be stored.', 1;
    END

    -- 5. Roll the inserted wagers into the per-player running totals.
    UPDATE pl
    SET    pl.TotalAmountSpent = pl.TotalAmountSpent + t.Amount,
           pl.TotalWagerCount  = pl.TotalWagerCount  + t.WagerCount
    FROM   dbo.Player AS pl
           INNER JOIN (
               SELECT b.AccountId, SUM(i.Amount) AS Amount, COUNT(*) AS WagerCount
               FROM   @Inserted AS i
                      INNER JOIN #Batch AS b ON b.WagerId = i.WagerId
               GROUP BY b.AccountId
           ) AS t ON t.AccountId = pl.AccountId;

    COMMIT TRANSACTION;

    SELECT COUNT(*) AS InsertedCount FROM @Inserted;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_CasinoWager_GetByPlayer
    @AccountId  UNIQUEIDENTIFIER,
    @Page       INT,
    @PageSize   INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @PlayerId INT, @Total INT = 0;

    SELECT @PlayerId = PlayerId,
           @Total    = TotalWagerCount
    FROM   dbo.Player
    WHERE  AccountId = @AccountId;

    -- Result set 1: total number of wagers for the player (drives totalPages).
    SELECT @Total AS Total;

    -- Result set 2: the requested page, latest first.
    SELECT  cw.WagerId,
            g.Name             AS Game,
            p.Name             AS Provider,
            cw.Amount,
            cw.CreatedDateTime AS CreatedDate
    FROM    dbo.CasinoWager AS cw
            INNER JOIN dbo.Game     AS g ON g.GameId     = cw.GameId
            INNER JOIN dbo.Provider AS p ON p.ProviderId = g.ProviderId
    WHERE   cw.PlayerId = @PlayerId
    ORDER BY cw.CreatedDateTime DESC, cw.CasinoWagerId DESC
    OFFSET (@Page - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_Player_GetTopSpenders
    @Count INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT TOP (@Count)
           AccountId,
           Username,
           TotalAmountSpent AS TotalAmountSpend
    FROM   dbo.Player
    WHERE  TotalWagerCount > 0
    ORDER BY TotalAmountSpent DESC, PlayerId ASC;
END
GO
