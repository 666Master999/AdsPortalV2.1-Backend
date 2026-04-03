IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE TABLE [Categories] (
        [Id] int NOT NULL IDENTITY,
        [Name] nvarchar(max) NOT NULL,
        [ParentId] int NULL,
        CONSTRAINT [PK_Categories] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Categories_Categories_ParentId] FOREIGN KEY ([ParentId]) REFERENCES [Categories] ([Id]) ON DELETE NO ACTION
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE TABLE [Users] (
        [Id] int NOT NULL IDENTITY,
        [UserLogin] nvarchar(max) NOT NULL,
        [UserPasswordHash] nvarchar(max) NOT NULL,
        [UserName] nvarchar(max) NULL,
        [UserEmail] nvarchar(max) NULL,
        [UserPhoneNumber] nvarchar(max) NULL,
        [AvatarPath] nvarchar(max) NULL,
        [IsAdmin] bit NOT NULL,
        [IsBlocked] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Users] PRIMARY KEY ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE TABLE [AdminLogs] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Action] nvarchar(max) NOT NULL,
        [Details] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_AdminLogs] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AdminLogs_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE TABLE [Ads] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [CategoryId] int NULL,
        [Title] nvarchar(max) NOT NULL,
        [Description] nvarchar(max) NULL,
        [Price] decimal(10,2) NULL,
        [City] nvarchar(max) NULL,
        [Type] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        [UpdatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Ads] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Ads_Categories_CategoryId] FOREIGN KEY ([CategoryId]) REFERENCES [Categories] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Ads_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE TABLE [UserBlocks] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Reason] nvarchar(max) NOT NULL,
        [BlockedAt] datetime2 NOT NULL,
        [UnblockedAt] datetime2 NULL,
        CONSTRAINT [PK_UserBlocks] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_UserBlocks_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE TABLE [UserReviews] (
        [Id] int NOT NULL IDENTITY,
        [ReviewerId] int NOT NULL,
        [TargetUserId] int NOT NULL,
        [Rating] int NOT NULL,
        [Text] nvarchar(max) NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_UserReviews] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_UserReviews_Users_ReviewerId] FOREIGN KEY ([ReviewerId]) REFERENCES [Users] ([Id]),
        CONSTRAINT [FK_UserReviews_Users_TargetUserId] FOREIGN KEY ([TargetUserId]) REFERENCES [Users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE TABLE [UserSessions] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Token] nvarchar(max) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        [ExpiresAt] datetime2 NULL,
        CONSTRAINT [PK_UserSessions] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_UserSessions_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE TABLE [AdImages] (
        [Id] int NOT NULL IDENTITY,
        [AdId] int NOT NULL,
        [FilePath] nvarchar(max) NOT NULL,
        [SortOrder] int NOT NULL,
        [IsMain] bit NOT NULL,
        CONSTRAINT [PK_AdImages] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AdImages_Ads_AdId] FOREIGN KEY ([AdId]) REFERENCES [Ads] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Name', N'ParentId') AND [object_id] = OBJECT_ID(N'[Categories]'))
        SET IDENTITY_INSERT [Categories] ON;
    EXEC(N'INSERT INTO [Categories] ([Id], [Name], [ParentId])
    VALUES (1, N''Электроника'', NULL),
    (2, N''Бытовая техника'', NULL),
    (3, N''Книги'', NULL),
    (4, N''Одежда'', NULL)');
    IF EXISTS (SELECT * FROM [sys].[identity_columns] WHERE [name] IN (N'Id', N'Name', N'ParentId') AND [object_id] = OBJECT_ID(N'[Categories]'))
        SET IDENTITY_INSERT [Categories] OFF;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AdImages_AdId] ON [AdImages] ([AdId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AdminLogs_UserId] ON [AdminLogs] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Ads_CategoryId] ON [Ads] ([CategoryId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Ads_UserId] ON [Ads] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Categories_ParentId] ON [Categories] ([ParentId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserBlocks_UserId] ON [UserBlocks] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserReviews_ReviewerId] ON [UserReviews] ([ReviewerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserReviews_TargetUserId] ON [UserReviews] ([TargetUserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_UserSessions_UserId] ON [UserSessions] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260311202310_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260311202310_InitialCreate', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260319142058_AddAdSoftDeleteAndModerationStatus'
)
BEGIN
    ALTER TABLE [Ads] ADD [IsDeleted] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260319142058_AddAdSoftDeleteAndModerationStatus'
)
BEGIN
    ALTER TABLE [Ads] ADD [ModerationStatus] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260319142058_AddAdSoftDeleteAndModerationStatus'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260319142058_AddAdSoftDeleteAndModerationStatus', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322160427_InitialMigration'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260322160427_InitialMigration', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322183911_AddMessagingSystem'
)
BEGIN
    CREATE TABLE [Conversations] (
        [Id] int NOT NULL IDENTITY,
        [SellerId] int NOT NULL,
        [BuyerId] int NOT NULL,
        [AdId] int NOT NULL,
        [FolderName] nvarchar(max) NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Conversations] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Conversations_Ads_AdId] FOREIGN KEY ([AdId]) REFERENCES [Ads] ([Id]),
        CONSTRAINT [FK_Conversations_Users_BuyerId] FOREIGN KEY ([BuyerId]) REFERENCES [Users] ([Id]),
        CONSTRAINT [FK_Conversations_Users_SellerId] FOREIGN KEY ([SellerId]) REFERENCES [Users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322183911_AddMessagingSystem'
)
BEGIN
    CREATE TABLE [Messages] (
        [Id] int NOT NULL IDENTITY,
        [ConversationId] int NOT NULL,
        [SenderId] int NOT NULL,
        [Text] nvarchar(max) NULL,
        [SentAt] datetime2 NOT NULL,
        [IsRead] bit NOT NULL,
        [ReadAt] datetime2 NULL,
        CONSTRAINT [PK_Messages] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Messages_Conversations_ConversationId] FOREIGN KEY ([ConversationId]) REFERENCES [Conversations] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_Messages_Users_SenderId] FOREIGN KEY ([SenderId]) REFERENCES [Users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322183911_AddMessagingSystem'
)
BEGIN
    CREATE TABLE [MessageAttachments] (
        [Id] int NOT NULL IDENTITY,
        [MessageId] int NOT NULL,
        [FilePath] nvarchar(max) NOT NULL,
        [OriginalFileName] nvarchar(max) NOT NULL,
        [ContentType] nvarchar(max) NULL,
        CONSTRAINT [PK_MessageAttachments] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_MessageAttachments_Messages_MessageId] FOREIGN KEY ([MessageId]) REFERENCES [Messages] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322183911_AddMessagingSystem'
)
BEGIN
    CREATE INDEX [IX_Conversations_AdId] ON [Conversations] ([AdId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322183911_AddMessagingSystem'
)
BEGIN
    CREATE INDEX [IX_Conversations_BuyerId] ON [Conversations] ([BuyerId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322183911_AddMessagingSystem'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Conversations_SellerId_BuyerId_AdId] ON [Conversations] ([SellerId], [BuyerId], [AdId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322183911_AddMessagingSystem'
)
BEGIN
    CREATE INDEX [IX_MessageAttachments_MessageId] ON [MessageAttachments] ([MessageId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322183911_AddMessagingSystem'
)
BEGIN
    CREATE INDEX [IX_Messages_ConversationId] ON [Messages] ([ConversationId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322183911_AddMessagingSystem'
)
BEGIN
    CREATE INDEX [IX_Messages_SenderId] ON [Messages] ([SenderId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322183911_AddMessagingSystem'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260322183911_AddMessagingSystem', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    DROP TABLE [MessageAttachments];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    DROP TABLE [Messages];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    EXEC sp_rename N'[Conversations].[FolderName]', N'DialogFolderPath', 'COLUMN';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [HasUnreadForBuyer] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [HasUnreadForSeller] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [IsArchivedForBuyer] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [IsArchivedForSeller] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [IsClosed] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [IsMutedForBuyer] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [IsMutedForSeller] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [LastClusterId] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [LastMessageAuthorId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [LastMessageText] nvarchar(max) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [LastMessageTimestamp] datetime2 NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [LastMessageType] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    ALTER TABLE [Conversations] ADD [TotalMessagesCount] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    CREATE INDEX [IX_Conversations_LastMessageTimestamp] ON [Conversations] ([LastMessageTimestamp]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322201029_RedesignMessagingSystem'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260322201029_RedesignMessagingSystem', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260322204308_UpdateConversations'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260322204308_UpdateConversations', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324070210_AddUserLastActivity'
)
BEGIN
    ALTER TABLE [Users] ADD [LastActivityAt] datetime2 NOT NULL DEFAULT '0001-01-01T00:00:00.0000000';
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324070210_AddUserLastActivity'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260324070210_AddUserLastActivity', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324134506_AddIsNegotiableToAd'
)
BEGIN
    ALTER TABLE [Ads] ADD [IsNegotiable] bit NOT NULL DEFAULT CAST(0 AS bit);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324134506_AddIsNegotiableToAd'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260324134506_AddIsNegotiableToAd', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181046_AddFavoritesAndCounters'
)
BEGIN
    ALTER TABLE [Ads] ADD [FavoritesCount] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181046_AddFavoritesAndCounters'
)
BEGIN
    ALTER TABLE [Ads] ADD [ViewsCount] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181046_AddFavoritesAndCounters'
)
BEGIN
    CREATE TABLE [UserFavoriteAds] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [AdId] int NOT NULL,
        [AddedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_UserFavoriteAds] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_UserFavoriteAds_Ads_AdId] FOREIGN KEY ([AdId]) REFERENCES [Ads] ([Id]),
        CONSTRAINT [FK_UserFavoriteAds_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181046_AddFavoritesAndCounters'
)
BEGIN
    CREATE INDEX [IX_UserFavoriteAds_AdId] ON [UserFavoriteAds] ([AdId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181046_AddFavoritesAndCounters'
)
BEGIN
    CREATE INDEX [IX_UserFavoriteAds_UserId] ON [UserFavoriteAds] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181046_AddFavoritesAndCounters'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260324181046_AddFavoritesAndCounters', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181853_FixAdsUserNoAction'
)
BEGIN
    ALTER TABLE [Ads] DROP CONSTRAINT [FK_Ads_Users_UserId];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181853_FixAdsUserNoAction'
)
BEGIN
    ALTER TABLE [Categories] DROP CONSTRAINT [FK_Categories_Categories_ParentId];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181853_FixAdsUserNoAction'
)
BEGIN
    ALTER TABLE [UserFavoriteAds] DROP CONSTRAINT [FK_UserFavoriteAds_Ads_AdId];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181853_FixAdsUserNoAction'
)
BEGIN
    ALTER TABLE [UserFavoriteAds] DROP CONSTRAINT [FK_UserFavoriteAds_Users_UserId];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181853_FixAdsUserNoAction'
)
BEGIN
    ALTER TABLE [Ads] ADD CONSTRAINT [FK_Ads_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181853_FixAdsUserNoAction'
)
BEGIN
    ALTER TABLE [Categories] ADD CONSTRAINT [FK_Categories_Categories_ParentId] FOREIGN KEY ([ParentId]) REFERENCES [Categories] ([Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181853_FixAdsUserNoAction'
)
BEGIN
    ALTER TABLE [UserFavoriteAds] ADD CONSTRAINT [FK_UserFavoriteAds_Ads_AdId] FOREIGN KEY ([AdId]) REFERENCES [Ads] ([Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181853_FixAdsUserNoAction'
)
BEGIN
    ALTER TABLE [UserFavoriteAds] ADD CONSTRAINT [FK_UserFavoriteAds_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324181853_FixAdsUserNoAction'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260324181853_FixAdsUserNoAction', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324185006_AddAdViews'
)
BEGIN
    CREATE TABLE [AdViews] (
        [Id] int NOT NULL IDENTITY,
        [AdId] int NOT NULL,
        [UserId] int NULL,
        [IpAddress] nvarchar(max) NULL,
        [ViewedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_AdViews] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AdViews_Ads_AdId] FOREIGN KEY ([AdId]) REFERENCES [Ads] ([Id])
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324185006_AddAdViews'
)
BEGIN
    CREATE INDEX [IX_AdViews_AdId_UserId] ON [AdViews] ([AdId], [UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324185006_AddAdViews'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260324185006_AddAdViews', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324185127_RemoveIpAddressFromAdView'
)
BEGIN
    DECLARE @var nvarchar(max);
    SELECT @var = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[AdViews]') AND [c].[name] = N'IpAddress');
    IF @var IS NOT NULL EXEC(N'ALTER TABLE [AdViews] DROP CONSTRAINT ' + @var + ';');
    ALTER TABLE [AdViews] DROP COLUMN [IpAddress];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324185127_RemoveIpAddressFromAdView'
)
BEGIN
    DROP INDEX [IX_AdViews_AdId_UserId] ON [AdViews];
    DECLARE @var1 nvarchar(max);
    SELECT @var1 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[AdViews]') AND [c].[name] = N'UserId');
    IF @var1 IS NOT NULL EXEC(N'ALTER TABLE [AdViews] DROP CONSTRAINT ' + @var1 + ';');
    EXEC(N'UPDATE [AdViews] SET [UserId] = 0 WHERE [UserId] IS NULL');
    ALTER TABLE [AdViews] ALTER COLUMN [UserId] int NOT NULL;
    ALTER TABLE [AdViews] ADD DEFAULT 0 FOR [UserId];
    CREATE INDEX [IX_AdViews_AdId_UserId] ON [AdViews] ([AdId], [UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260324185127_RemoveIpAddressFromAdView'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260324185127_RemoveIpAddressFromAdView', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260326140854_AddNotifications'
)
BEGIN
    DROP TABLE [AdViews];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260326140854_AddNotifications'
)
BEGIN
    CREATE TABLE [Notifications] (
        [Id] int NOT NULL IDENTITY,
        [UserId] int NOT NULL,
        [Type] int NOT NULL,
        [AdId] int NULL,
        [Message] nvarchar(max) NULL,
        [IsRead] bit NOT NULL,
        [CreatedAt] datetime2 NOT NULL,
        CONSTRAINT [PK_Notifications] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_Notifications_Ads_AdId] FOREIGN KEY ([AdId]) REFERENCES [Ads] ([Id]) ON DELETE SET NULL,
        CONSTRAINT [FK_Notifications_Users_UserId] FOREIGN KEY ([UserId]) REFERENCES [Users] ([Id]) ON DELETE CASCADE
    );
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260326140854_AddNotifications'
)
BEGIN
    CREATE INDEX [IX_Notifications_AdId] ON [Notifications] ([AdId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260326140854_AddNotifications'
)
BEGIN
    CREATE INDEX [IX_Notifications_UserId] ON [Notifications] ([UserId]);
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260326140854_AddNotifications'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260326140854_AddNotifications', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260330192811_ApplyReadViewedRefactor'
)
BEGIN
    DECLARE @var2 nvarchar(max);
    SELECT @var2 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Conversations]') AND [c].[name] = N'HasUnreadForBuyer');
    IF @var2 IS NOT NULL EXEC(N'ALTER TABLE [Conversations] DROP CONSTRAINT ' + @var2 + ';');
    ALTER TABLE [Conversations] DROP COLUMN [HasUnreadForBuyer];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260330192811_ApplyReadViewedRefactor'
)
BEGIN
    DECLARE @var3 nvarchar(max);
    SELECT @var3 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Conversations]') AND [c].[name] = N'HasUnreadForSeller');
    IF @var3 IS NOT NULL EXEC(N'ALTER TABLE [Conversations] DROP CONSTRAINT ' + @var3 + ';');
    ALTER TABLE [Conversations] DROP COLUMN [HasUnreadForSeller];
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260330192811_ApplyReadViewedRefactor'
)
BEGIN
    ALTER TABLE [Conversations] ADD [BuyerLastReadMessageId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260330192811_ApplyReadViewedRefactor'
)
BEGIN
    ALTER TABLE [Conversations] ADD [BuyerLastViewedMessageId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260330192811_ApplyReadViewedRefactor'
)
BEGIN
    ALTER TABLE [Conversations] ADD [BuyerUnreadCount] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260330192811_ApplyReadViewedRefactor'
)
BEGIN
    ALTER TABLE [Conversations] ADD [SellerLastReadMessageId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260330192811_ApplyReadViewedRefactor'
)
BEGIN
    ALTER TABLE [Conversations] ADD [SellerLastViewedMessageId] int NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260330192811_ApplyReadViewedRefactor'
)
BEGIN
    ALTER TABLE [Conversations] ADD [SellerUnreadCount] int NOT NULL DEFAULT 0;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260330192811_ApplyReadViewedRefactor'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260330192811_ApplyReadViewedRefactor', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260330213107_AddUnreadColumns'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260330213107_AddUnreadColumns', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260330213609_AddUnreadColumnsSSSSSSS'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260330213609_AddUnreadColumnsSSSSSSS', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260330213651_AddUnreadColumnsSSSS'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260330213651_AddUnreadColumnsSSSS', N'10.0.3');
END;

COMMIT;
GO

BEGIN TRANSACTION;
IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260331223244_HellEah'
)
BEGIN
    DECLARE @var4 nvarchar(max);
    SELECT @var4 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Users]') AND [c].[name] = N'UserPhoneNumber');
    IF @var4 IS NOT NULL EXEC(N'ALTER TABLE [Users] DROP CONSTRAINT ' + @var4 + ';');
    ALTER TABLE [Users] ALTER COLUMN [UserPhoneNumber] nvarchar(30) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260331223244_HellEah'
)
BEGIN
    DECLARE @var5 nvarchar(max);
    SELECT @var5 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Users]') AND [c].[name] = N'UserName');
    IF @var5 IS NOT NULL EXEC(N'ALTER TABLE [Users] DROP CONSTRAINT ' + @var5 + ';');
    ALTER TABLE [Users] ALTER COLUMN [UserName] nvarchar(100) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260331223244_HellEah'
)
BEGIN
    DECLARE @var6 nvarchar(max);
    SELECT @var6 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Users]') AND [c].[name] = N'UserLogin');
    IF @var6 IS NOT NULL EXEC(N'ALTER TABLE [Users] DROP CONSTRAINT ' + @var6 + ';');
    ALTER TABLE [Users] ALTER COLUMN [UserLogin] nvarchar(50) NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260331223244_HellEah'
)
BEGIN
    DECLARE @var7 nvarchar(max);
    SELECT @var7 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Users]') AND [c].[name] = N'UserEmail');
    IF @var7 IS NOT NULL EXEC(N'ALTER TABLE [Users] DROP CONSTRAINT ' + @var7 + ';');
    ALTER TABLE [Users] ALTER COLUMN [UserEmail] nvarchar(200) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260331223244_HellEah'
)
BEGIN
    DECLARE @var8 nvarchar(max);
    SELECT @var8 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Ads]') AND [c].[name] = N'Type');
    IF @var8 IS NOT NULL EXEC(N'ALTER TABLE [Ads] DROP CONSTRAINT ' + @var8 + ';');
    ALTER TABLE [Ads] ALTER COLUMN [Type] nvarchar(50) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260331223244_HellEah'
)
BEGIN
    DECLARE @var9 nvarchar(max);
    SELECT @var9 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Ads]') AND [c].[name] = N'Title');
    IF @var9 IS NOT NULL EXEC(N'ALTER TABLE [Ads] DROP CONSTRAINT ' + @var9 + ';');
    ALTER TABLE [Ads] ALTER COLUMN [Title] nvarchar(200) NOT NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260331223244_HellEah'
)
BEGIN
    DECLARE @var10 nvarchar(max);
    SELECT @var10 = QUOTENAME([d].[name])
    FROM [sys].[default_constraints] [d]
    INNER JOIN [sys].[columns] [c] ON [d].[parent_column_id] = [c].[column_id] AND [d].[parent_object_id] = [c].[object_id]
    WHERE ([d].[parent_object_id] = OBJECT_ID(N'[Ads]') AND [c].[name] = N'City');
    IF @var10 IS NOT NULL EXEC(N'ALTER TABLE [Ads] DROP CONSTRAINT ' + @var10 + ';');
    ALTER TABLE [Ads] ALTER COLUMN [City] nvarchar(100) NULL;
END;

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20260331223244_HellEah'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20260331223244_HellEah', N'10.0.3');
END;

COMMIT;
GO

