-- Migration administrator, after application and Hangfire schema installation.
-- Add the IIS service account database user to SsrsSubscriptionRuntime separately.
-- Never add the runtime account to db_owner, db_ddladmin or the administrator role.
SET XACT_ABORT ON;
IF SCHEMA_ID(N'HangFire') IS NULL OR NOT EXISTS(SELECT 1 FROM [subscriptions].[SchemaVersion] WHERE [Id]=1 AND [Version]=1)
    THROW 51001, 'Install subscription and Hangfire schemas first.', 1;
GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[HangFire] TO [SsrsSubscriptionRuntime];
