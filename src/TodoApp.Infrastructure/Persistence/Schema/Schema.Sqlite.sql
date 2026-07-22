-- SQLite schema for TodoApp. Idempotent (CREATE ... IF NOT EXISTS) so it can run on every
-- startup and against the shared in-memory test database. Column names/types mirror the
-- schema EF Core produced: DateTimeOffset is stored as UTC-tick INTEGER, enums as INTEGER,
-- the concurrency token as TEXT.

CREATE TABLE IF NOT EXISTS Users (
    Id             INTEGER NOT NULL CONSTRAINT PK_Users PRIMARY KEY AUTOINCREMENT,
    Email          TEXT    NOT NULL,
    PasswordHash   TEXT    NULL,
    Role           INTEGER NOT NULL,
    SecurityStamp  TEXT    NOT NULL,
    IsActive       INTEGER NOT NULL,
    CreatedAt      INTEGER NOT NULL,
    UpdatedAt      INTEGER NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Email ON Users (Email);

CREATE TABLE IF NOT EXISTS Categories (
    Id         INTEGER NOT NULL CONSTRAINT PK_Categories PRIMARY KEY AUTOINCREMENT,
    UserId     INTEGER NOT NULL,
    Name       TEXT    NOT NULL,
    Color      TEXT    NOT NULL,
    CreatedAt  INTEGER NOT NULL,
    UpdatedAt  INTEGER NULL,
    CONSTRAINT FK_Categories_Users_UserId FOREIGN KEY (UserId) REFERENCES Users (Id)
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_Categories_UserId_Name ON Categories (UserId, Name);

CREATE TABLE IF NOT EXISTS TodoItems (
    Id                INTEGER NOT NULL CONSTRAINT PK_TodoItems PRIMARY KEY AUTOINCREMENT,
    UserId            INTEGER NOT NULL,
    Title             TEXT    NOT NULL,
    Description       TEXT    NULL,
    Status            INTEGER NOT NULL,
    CategoryId        INTEGER NULL,
    Priority          INTEGER NOT NULL,
    DueDate           INTEGER NULL,
    ConcurrencyToken  TEXT    NOT NULL,
    CreatedAt         INTEGER NOT NULL,
    UpdatedAt         INTEGER NULL,
    CONSTRAINT FK_TodoItems_Users_UserId FOREIGN KEY (UserId) REFERENCES Users (Id) ON DELETE CASCADE,
    CONSTRAINT FK_TodoItems_Categories_CategoryId FOREIGN KEY (CategoryId) REFERENCES Categories (Id) ON DELETE SET NULL
);
CREATE INDEX IF NOT EXISTS IX_TodoItems_UserId ON TodoItems (UserId);
CREATE INDEX IF NOT EXISTS IX_TodoItems_UserId_Status ON TodoItems (UserId, Status);
CREATE INDEX IF NOT EXISTS IX_TodoItems_CategoryId ON TodoItems (CategoryId);

CREATE TABLE IF NOT EXISTS RefreshTokens (
    Id                    INTEGER NOT NULL CONSTRAINT PK_RefreshTokens PRIMARY KEY AUTOINCREMENT,
    UserId                INTEGER NOT NULL,
    TokenHash             TEXT    NOT NULL,
    ExpiresAt             INTEGER NOT NULL,
    RevokedAt             INTEGER NULL,
    RevokedReason         TEXT    NULL,
    ReplacedByTokenHash   TEXT    NULL,
    CreatedAt             INTEGER NOT NULL,
    UpdatedAt             INTEGER NULL,
    CONSTRAINT FK_RefreshTokens_Users_UserId FOREIGN KEY (UserId) REFERENCES Users (Id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_RefreshTokens_TokenHash ON RefreshTokens (TokenHash);
CREATE INDEX IF NOT EXISTS IX_RefreshTokens_UserId ON RefreshTokens (UserId);

CREATE TABLE IF NOT EXISTS ExternalLogins (
    Id           INTEGER NOT NULL CONSTRAINT PK_ExternalLogins PRIMARY KEY AUTOINCREMENT,
    UserId       INTEGER NOT NULL,
    Provider     TEXT    NOT NULL,
    ProviderKey  TEXT    NOT NULL,
    CreatedAt    INTEGER NOT NULL,
    UpdatedAt    INTEGER NULL,
    CONSTRAINT FK_ExternalLogins_Users_UserId FOREIGN KEY (UserId) REFERENCES Users (Id) ON DELETE CASCADE
);
CREATE UNIQUE INDEX IF NOT EXISTS IX_ExternalLogins_Provider_ProviderKey ON ExternalLogins (Provider, ProviderKey);
CREATE INDEX IF NOT EXISTS IX_ExternalLogins_UserId ON ExternalLogins (UserId);
