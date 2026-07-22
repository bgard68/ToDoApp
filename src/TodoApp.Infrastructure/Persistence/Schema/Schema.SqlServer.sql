-- SQL Server / Azure SQL schema for TodoApp. Each table is guarded by an existence check so
-- the script is idempotent and safe to run at startup — including against the shared production
-- database whose tables were originally created by EF Core (matching names/types, so the guards
-- simply skip creation). No GO batch separators: the whole script runs as one command.
--
-- Category.UserId uses NO ACTION (not cascade) on purpose: a cascade there would give the User
-- table two cascade paths into TodoItems, which SQL Server rejects. This mirrors the old EF
-- ClientCascade configuration.

IF OBJECT_ID(N'dbo.Users', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Users (
        Id             INT            IDENTITY(1,1) NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
        Email          NVARCHAR(256)  NOT NULL,
        PasswordHash   NVARCHAR(MAX)  NULL,
        Role           INT            NOT NULL,
        SecurityStamp  NVARCHAR(64)   NOT NULL,
        IsActive       BIT            NOT NULL,
        CreatedAt      BIGINT         NOT NULL,
        UpdatedAt      BIGINT         NULL
    );
    CREATE UNIQUE INDEX IX_Users_Email ON dbo.Users (Email);
END;

IF OBJECT_ID(N'dbo.Categories', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Categories (
        Id         INT           IDENTITY(1,1) NOT NULL CONSTRAINT PK_Categories PRIMARY KEY,
        UserId     INT           NOT NULL,
        Name       NVARCHAR(50)  NOT NULL,
        Color      NVARCHAR(32)  NOT NULL,
        CreatedAt  BIGINT        NOT NULL,
        UpdatedAt  BIGINT        NULL,
        CONSTRAINT FK_Categories_Users_UserId FOREIGN KEY (UserId) REFERENCES dbo.Users (Id)
    );
    CREATE UNIQUE INDEX IX_Categories_UserId_Name ON dbo.Categories (UserId, Name);
END;

IF OBJECT_ID(N'dbo.TodoItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TodoItems (
        Id                INT               IDENTITY(1,1) NOT NULL CONSTRAINT PK_TodoItems PRIMARY KEY,
        UserId            INT               NOT NULL,
        Title             NVARCHAR(200)     NOT NULL,
        Description       NVARCHAR(2000)    NULL,
        Status            INT               NOT NULL,
        CategoryId        INT               NULL,
        Priority          INT               NOT NULL,
        DueDate           BIGINT            NULL,
        ConcurrencyToken  UNIQUEIDENTIFIER  NOT NULL,
        CreatedAt         BIGINT            NOT NULL,
        UpdatedAt         BIGINT            NULL,
        CONSTRAINT FK_TodoItems_Users_UserId FOREIGN KEY (UserId) REFERENCES dbo.Users (Id) ON DELETE CASCADE,
        CONSTRAINT FK_TodoItems_Categories_CategoryId FOREIGN KEY (CategoryId) REFERENCES dbo.Categories (Id) ON DELETE SET NULL
    );
    CREATE INDEX IX_TodoItems_UserId ON dbo.TodoItems (UserId);
    CREATE INDEX IX_TodoItems_UserId_Status ON dbo.TodoItems (UserId, Status);
    CREATE INDEX IX_TodoItems_CategoryId ON dbo.TodoItems (CategoryId);
END;

IF OBJECT_ID(N'dbo.RefreshTokens', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.RefreshTokens (
        Id                    INT            IDENTITY(1,1) NOT NULL CONSTRAINT PK_RefreshTokens PRIMARY KEY,
        UserId                INT            NOT NULL,
        TokenHash             NVARCHAR(128)  NOT NULL,
        ExpiresAt             BIGINT         NOT NULL,
        RevokedAt             BIGINT         NULL,
        RevokedReason         NVARCHAR(200)  NULL,
        ReplacedByTokenHash   NVARCHAR(128)  NULL,
        CreatedAt             BIGINT         NOT NULL,
        UpdatedAt             BIGINT         NULL,
        CONSTRAINT FK_RefreshTokens_Users_UserId FOREIGN KEY (UserId) REFERENCES dbo.Users (Id) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX IX_RefreshTokens_TokenHash ON dbo.RefreshTokens (TokenHash);
    CREATE INDEX IX_RefreshTokens_UserId ON dbo.RefreshTokens (UserId);
END;

IF OBJECT_ID(N'dbo.ExternalLogins', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ExternalLogins (
        Id           INT            IDENTITY(1,1) NOT NULL CONSTRAINT PK_ExternalLogins PRIMARY KEY,
        UserId       INT            NOT NULL,
        Provider     NVARCHAR(50)   NOT NULL,
        ProviderKey  NVARCHAR(256)  NOT NULL,
        CreatedAt    BIGINT         NOT NULL,
        UpdatedAt    BIGINT         NULL,
        CONSTRAINT FK_ExternalLogins_Users_UserId FOREIGN KEY (UserId) REFERENCES dbo.Users (Id) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX IX_ExternalLogins_Provider_ProviderKey ON dbo.ExternalLogins (Provider, ProviderKey);
    CREATE INDEX IX_ExternalLogins_UserId ON dbo.ExternalLogins (UserId);
END;
