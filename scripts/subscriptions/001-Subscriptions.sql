-- Apply to a separately provisioned [SsrsWeb] database as migration administrator.
-- No runtime DDL, automatic upgrade, destructive migration or report-output storage.
SET XACT_ABORT ON;
SET NOCOUNT ON;
BEGIN TRANSACTION;
DECLARE @lockResult int;
EXEC @lockResult = sys.sp_getapplock @Resource=N'ssrs-subscriptions-migration', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000;
IF @lockResult < 0 THROW 51000, 'Subscription migration lock unavailable.', 1;
IF SCHEMA_ID(N'subscriptions') IS NULL EXEC(N'CREATE SCHEMA [subscriptions] AUTHORIZATION [dbo];');
IF OBJECT_ID(N'[subscriptions].[SchemaVersion]', N'U') IS NULL
BEGIN
    CREATE TABLE [subscriptions].[SchemaVersion] ([Id] int NOT NULL CONSTRAINT [PK_SubscriptionSchemaVersion] PRIMARY KEY CHECK ([Id]=1), [Version] int NOT NULL);
    INSERT [subscriptions].[SchemaVersion] ([Id],[Version]) VALUES (1,0);
END;
IF (SELECT [Version] FROM [subscriptions].[SchemaVersion] WHERE [Id]=1) NOT IN (0,1)
    THROW 51001, 'Unsupported subscription schema version. Use the matching application/migration.', 1;
IF (SELECT [Version] FROM [subscriptions].[SchemaVersion] WHERE [Id]=1)=0
BEGIN
    CREATE TABLE [subscriptions].[Owner] (
        [TenantId] uniqueidentifier NOT NULL, [UserObjectId] uniqueidentifier NOT NULL,
        [UserEmail] nvarchar(254) NULL, [Revoked] bit NOT NULL CONSTRAINT [DF_SubscriptionOwnerRevoked] DEFAULT(0),
        [UpdatedUtc] datetimeoffset(7) NOT NULL,
        CONSTRAINT [PK_SubscriptionOwner] PRIMARY KEY ([TenantId],[UserObjectId]));
    CREATE TABLE [subscriptions].[Subscription] (
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_Subscription] PRIMARY KEY,
        [TenantId] uniqueidentifier NOT NULL, [UserObjectId] uniqueidentifier NOT NULL,
        [Revision] int NOT NULL, [Enabled] bit NOT NULL, [Deleted] bit NOT NULL,
        [DefinitionJson] nvarchar(max) NOT NULL CHECK (ISJSON([DefinitionJson])=1),
        [NextDueUtc] datetimeoffset(7) NULL, [UpdatedUtc] datetimeoffset(7) NOT NULL,
        CONSTRAINT [FK_SubscriptionOwner] FOREIGN KEY ([TenantId],[UserObjectId]) REFERENCES [subscriptions].[Owner]([TenantId],[UserObjectId]));
    CREATE INDEX [IX_SubscriptionOwner] ON [subscriptions].[Subscription]([TenantId],[UserObjectId],[Deleted]) INCLUDE ([Enabled]);
    CREATE INDEX [IX_SubscriptionDue] ON [subscriptions].[Subscription]([NextDueUtc]) INCLUDE ([Revision],[TenantId],[UserObjectId]) WHERE [Enabled]=1 AND [Deleted]=0;
    CREATE TABLE [subscriptions].[Execution] (
        [Id] uniqueidentifier NOT NULL CONSTRAINT [PK_SubscriptionExecution] PRIMARY KEY,
        [SubscriptionId] uniqueidentifier NOT NULL, [Revision] int NOT NULL,
        [OccurrenceUtc] datetimeoffset(7) NOT NULL, [Status] varchar(16) NOT NULL,
        [Attempts] int NOT NULL CONSTRAINT [DF_SubscriptionExecutionAttempts] DEFAULT(0),
        [AvailableUtc] datetimeoffset(7) NOT NULL, [StartedUtc] datetimeoffset(7) NULL,
        [FinishedUtc] datetimeoffset(7) NULL, [SendStartedUtc] datetimeoffset(7) NULL,
        [ProviderMessageId] varchar(100) NULL, [Diagnostic] varchar(100) NULL,
        CONSTRAINT [FK_SubscriptionExecution] FOREIGN KEY ([SubscriptionId]) REFERENCES [subscriptions].[Subscription]([Id]),
        CONSTRAINT [UQ_SubscriptionOccurrence] UNIQUE ([SubscriptionId],[OccurrenceUtc]),
        CONSTRAINT [CK_SubscriptionExecutionStatus] CHECK ([Status] IN ('Pending','Running','Delivered','Failed','Unknown','Skipped')));
    CREATE INDEX [IX_SubscriptionWork] ON [subscriptions].[Execution]([Status],[AvailableUtc]) INCLUDE ([SubscriptionId]);
    CREATE INDEX [IX_SubscriptionHistory] ON [subscriptions].[Execution]([SubscriptionId],[OccurrenceUtc] DESC);
    CREATE TABLE [subscriptions].[OwnerAudit] (
        [Id] bigint IDENTITY NOT NULL CONSTRAINT [PK_SubscriptionOwnerAudit] PRIMARY KEY,
        [TenantId] uniqueidentifier NOT NULL, [UserObjectId] uniqueidentifier NOT NULL,
        [Revoked] bit NOT NULL, [Reason] nvarchar(1000) NOT NULL,
        [Actor] sysname NOT NULL, [AtUtc] datetimeoffset(7) NOT NULL);
    UPDATE [subscriptions].[SchemaVersion] SET [Version]=1 WHERE [Id]=1;
END;
IF DATABASE_PRINCIPAL_ID(N'SsrsSubscriptionRuntime') IS NULL CREATE ROLE [SsrsSubscriptionRuntime] AUTHORIZATION [dbo];
IF DATABASE_PRINCIPAL_ID(N'SsrsSubscriptionAdministrator') IS NULL CREATE ROLE [SsrsSubscriptionAdministrator] AUTHORIZATION [dbo];
COMMIT;
GO
CREATE OR ALTER PROCEDURE [subscriptions].[TouchOwner]
    @TenantId uniqueidentifier, @UserObjectId uniqueidentifier, @UserEmail nvarchar(254)
AS
BEGIN
    SET XACT_ABORT ON; SET NOCOUNT ON;
    IF EXISTS(SELECT 1 FROM [subscriptions].[Owner] WHERE [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId)
    BEGIN
        UPDATE [subscriptions].[Owner] SET [UserEmail]=COALESCE(@UserEmail,[UserEmail]),[UpdatedUtc]=SYSUTCDATETIME() WHERE [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId;
        RETURN;
    END;
    BEGIN TRANSACTION;
    DECLARE @resource nvarchar(255)=N'ssrs-owner:'+LOWER(CONVERT(nvarchar(36),@TenantId))+N':'+LOWER(CONVERT(nvarchar(36),@UserObjectId)), @result int;
    EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=10000;
    IF @result < 0 THROW 51000,'Owner lock unavailable.',1;
    IF NOT EXISTS(SELECT 1 FROM [subscriptions].[Owner] WHERE [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId)
        INSERT [subscriptions].[Owner]([TenantId],[UserObjectId],[UserEmail],[Revoked],[UpdatedUtc]) VALUES(@TenantId,@UserObjectId,@UserEmail,0,SYSUTCDATETIME());
    ELSE
        UPDATE [subscriptions].[Owner] SET [UserEmail]=COALESCE(@UserEmail,[UserEmail]),[UpdatedUtc]=SYSUTCDATETIME() WHERE [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId;
    COMMIT;
END;
GO
CREATE OR ALTER PROCEDURE [dbo].[RevokeSubscriptionsForEntraUser]
    @TenantId uniqueidentifier, @UserObjectId uniqueidentifier, @Reason nvarchar(1000)
AS
BEGIN
    SET XACT_ABORT ON; SET NOCOUNT ON;
    IF @TenantId IS NULL OR @UserObjectId IS NULL OR @TenantId='00000000-0000-0000-0000-000000000000' OR @UserObjectId='00000000-0000-0000-0000-000000000000' OR NULLIF(LTRIM(RTRIM(@Reason)),N'') IS NULL
        THROW 51002,'Tenant, object identity and administrative reason are required.',1;
    BEGIN TRANSACTION;
    -- The delivery session holds this same gate through Postmark acceptance and outcome recording.
    DECLARE @resource nvarchar(255)=N'ssrs-owner:'+LOWER(CONVERT(nvarchar(36),@TenantId))+N':'+LOWER(CONVERT(nvarchar(36),@UserObjectId)), @result int;
    EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=180000;
    IF @result < 0 THROW 51000,'Owner delivery gate unavailable; revocation did not complete.',1;
    IF NOT EXISTS(SELECT 1 FROM [subscriptions].[Owner] WHERE [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId)
        INSERT [subscriptions].[Owner]([TenantId],[UserObjectId],[Revoked],[UpdatedUtc]) VALUES(@TenantId,@UserObjectId,1,SYSUTCDATETIME());
    ELSE UPDATE [subscriptions].[Owner] SET [Revoked]=1,[UpdatedUtc]=SYSUTCDATETIME() WHERE [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId;
    UPDATE [subscriptions].[Subscription] SET [Enabled]=0,[NextDueUtc]=NULL,[Revision]=[Revision]+1,[UpdatedUtc]=SYSUTCDATETIME() WHERE [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId AND [Enabled]=1;
    UPDATE e SET [Status]='Skipped',[Diagnostic]='owner-revoked',[FinishedUtc]=SYSUTCDATETIME()
        FROM [subscriptions].[Execution] e INNER JOIN [subscriptions].[Subscription] s ON s.[Id]=e.[SubscriptionId]
        WHERE s.[TenantId]=@TenantId AND s.[UserObjectId]=@UserObjectId AND e.[Status]='Pending';
    INSERT [subscriptions].[OwnerAudit]([TenantId],[UserObjectId],[Revoked],[Reason],[Actor],[AtUtc]) VALUES(@TenantId,@UserObjectId,1,@Reason,ORIGINAL_LOGIN(),SYSUTCDATETIME());
    COMMIT;
END;
GO
CREATE OR ALTER PROCEDURE [dbo].[ReinstateSubscriptionsForEntraUser]
    @TenantId uniqueidentifier, @UserObjectId uniqueidentifier, @Reason nvarchar(1000)
AS
BEGIN
    SET XACT_ABORT ON; SET NOCOUNT ON;
    IF @TenantId IS NULL OR @UserObjectId IS NULL OR NULLIF(LTRIM(RTRIM(@Reason)),N'') IS NULL THROW 51002,'Identity and reason required.',1;
    BEGIN TRANSACTION;
    DECLARE @resource nvarchar(255)=N'ssrs-owner:'+LOWER(CONVERT(nvarchar(36),@TenantId))+N':'+LOWER(CONVERT(nvarchar(36),@UserObjectId)), @result int;
    EXEC @result=sys.sp_getapplock @Resource=@resource,@LockMode='Exclusive',@LockOwner='Transaction',@LockTimeout=180000;
    IF @result < 0 THROW 51000,'Owner gate unavailable.',1;
    UPDATE [subscriptions].[Owner] SET [Revoked]=0,[UpdatedUtc]=SYSUTCDATETIME() WHERE [TenantId]=@TenantId AND [UserObjectId]=@UserObjectId;
    -- Existing subscriptions stay paused. An authenticated owner must explicitly resume.
    INSERT [subscriptions].[OwnerAudit]([TenantId],[UserObjectId],[Revoked],[Reason],[Actor],[AtUtc]) VALUES(@TenantId,@UserObjectId,0,@Reason,ORIGINAL_LOGIN(),SYSUTCDATETIME());
    COMMIT;
END;
GO
GRANT SELECT ON [subscriptions].[SchemaVersion] TO [SsrsSubscriptionRuntime];
GRANT SELECT ON [subscriptions].[Owner] TO [SsrsSubscriptionRuntime];
GRANT EXECUTE ON [subscriptions].[TouchOwner] TO [SsrsSubscriptionRuntime];
GRANT SELECT, INSERT, UPDATE ON [subscriptions].[Subscription] TO [SsrsSubscriptionRuntime];
GRANT SELECT, INSERT, UPDATE ON [subscriptions].[Execution] TO [SsrsSubscriptionRuntime];
GRANT SELECT ON [subscriptions].[OwnerAudit] TO [SsrsSubscriptionAdministrator];
GRANT SELECT ON SCHEMA::[subscriptions] TO [SsrsSubscriptionAdministrator];
GRANT EXECUTE ON [dbo].[RevokeSubscriptionsForEntraUser] TO [SsrsSubscriptionAdministrator];
GRANT EXECUTE ON [dbo].[ReinstateSubscriptionsForEntraUser] TO [SsrsSubscriptionAdministrator];
GO
