/*
 Optional, read-only upgrade diagnostic; never called by the application.
 Works with master selected in SSMS. No credentials, source configuration or RDL
 are returned. Compare metadata with docs/operations/catalog-monitor.md and the fixed snapshot SQL.
 Counts are diagnostic observations, not a transactionally consistent snapshot.
 SqlServerEngineVersion is the database engine version, NOT the SSRS product build.
*/
SET NOCOUNT ON;

SELECT CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')) AS SqlServerEngineVersion,
    N'ReportServer' AS TargetDatabase;

SELECT t.name AS TableName, c.column_id AS ColumnOrdinal, c.name AS ColumnName,
    ty.name AS DeclaredType, c.max_length AS MaxLengthBytes, c.is_nullable AS IsNullable
FROM [ReportServer].[sys].[tables] AS t
JOIN [ReportServer].[sys].[schemas] AS s ON s.schema_id = t.schema_id
JOIN [ReportServer].[sys].[columns] AS c ON c.object_id = t.object_id
JOIN [ReportServer].[sys].[types] AS ty ON ty.user_type_id = c.user_type_id
WHERE s.name = N'dbo' AND t.name IN (N'Catalog', N'DataSource')
ORDER BY t.name, c.column_id;

SELECT COUNT_BIG(*) AS CatalogRows,
    COUNT_BIG(CASE WHEN [Hidden] IS NULL THEN 1 END) AS NullHiddenRows
FROM [ReportServer].[dbo].[Catalog];

SELECT COUNT_BIG(*) AS DataSourceRows,
    COUNT_BIG(CASE WHEN ds.[Link] IS NOT NULL AND c.[ItemID] IS NULL THEN 1 END) AS UnresolvedSharedLinks,
    COUNT_BIG(CASE WHEN ds.[Link] IS NOT NULL AND c.[Type] <> 5 THEN 1 END) AS NonSourceSharedLinks
FROM [ReportServer].[dbo].[DataSource] AS ds
LEFT JOIN [ReportServer].[dbo].[Catalog] AS c ON c.[ItemID] = ds.[Link];
