/*
 Read-only change detector for the verified ReportServer schema on SQL Server 2022.
 SOAP remains authoritative for metadata, visibility, authorization and execution.

 Application parameters: @MaximumCatalogRows int, @MaximumDataSourceRows int.
 For SSMS, declare these variables (normally 6000 and 18000) and run the entire
 batch inside a short SERIALIZABLE transaction. The application supplies that
 transaction and rejects incomplete result sets or the extra row detecting a limit.

 Results: catalog metadata, then source IDs/relationships plus a SHA-256 fingerprint.
 No RDL, connection strings, usernames or passwords leave SQL Server.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

SELECT TOP (@MaximumCatalogRows + 1)
    [ItemID], [Path], [Type], [ModifiedDate], [Hidden], [LinkSourceID]
FROM [ReportServer].[dbo].[Catalog]
ORDER BY [ItemID];

-- Explicit field order below is the fingerprint contract. Each field contributes
-- 0x00 for NULL, or 0x01 followed by its fixed 32-byte SHA-256. This keeps NULL
-- distinct from empty and preserves field boundaries. The outer hash covers all
-- frames; IDs/owner/link are compared directly from the returned columns.
-- Max conversions preserve large image/ntext values before hashing; ntext becomes
-- nvarchar(max) first. Start concatenation with varbinary(max) to avoid truncation.
SELECT TOP (@MaximumDataSourceRows + 1)
    [DSID], [ItemID], [Link],
    HASHBYTES('SHA2_256', CONVERT(varbinary(max), 0x)
        + CASE WHEN [SubscriptionID] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), [SubscriptionID])) END
        + CASE WHEN [Name] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), CONVERT(nvarchar(max), [Name]))) END
        + CASE WHEN [Extension] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), CONVERT(nvarchar(max), [Extension]))) END
        + CASE WHEN [CredentialRetrieval] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), [CredentialRetrieval])) END
        + CASE WHEN [Prompt] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), CONVERT(nvarchar(max), [Prompt]))) END
        + CASE WHEN [ConnectionString] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), [ConnectionString])) END
        + CASE WHEN [OriginalConnectionString] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), [OriginalConnectionString])) END
        + CASE WHEN [OriginalConnectStringExpressionBased] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), [OriginalConnectStringExpressionBased])) END
        + CASE WHEN [UserName] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), [UserName])) END
        + CASE WHEN [Password] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), [Password])) END
        + CASE WHEN [Flags] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), [Flags])) END
        + CASE WHEN [Version] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), [Version])) END
        + CASE WHEN [DSIDNum] IS NULL THEN 0x00 ELSE 0x01
            + HASHBYTES('SHA2_256', CONVERT(varbinary(max), [DSIDNum])) END
    ) AS [Fingerprint]
FROM [ReportServer].[dbo].[DataSource]
ORDER BY [DSID];
