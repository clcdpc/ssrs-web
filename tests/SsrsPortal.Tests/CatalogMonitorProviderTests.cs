using System.Collections;
using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

public sealed class CatalogMonitorProviderTests
{
    private static DataTable[] Tables()
    {
        var catalog = new DataTable();
        catalog.Columns.Add("ItemID", typeof(Guid));
        catalog.Columns.Add("Path", typeof(string));
        catalog.Columns.Add("Type", typeof(int));
        catalog.Columns.Add("ModifiedDate", typeof(DateTime));
        catalog.Columns.Add("Hidden", typeof(bool));
        catalog.Columns.Add("LinkSourceID", typeof(Guid));
        catalog.Rows.Add(Guid.Parse("8618808d-54cd-4b15-b41e-9f2db8b07444"), "/Training/Reports/A", 2,
            new DateTime(2026, 10, 10), false, DBNull.Value);
        var sources = new DataTable();
        sources.Columns.Add("DSID", typeof(Guid));
        sources.Columns.Add("ItemID", typeof(Guid));
        sources.Columns.Add("Link", typeof(Guid));
        sources.Columns.Add("Fingerprint", typeof(byte[]));
        sources.Rows.Add(Guid.Parse("b7c25b56-248b-450d-ad05-91a88182161b"), catalog.Rows[0][0], DBNull.Value,
            Convert.FromHexString(MonitorHarness.Fingerprint("synthetic source configuration")));
        return [catalog, sources];
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionReaderConsumesTwoCompleteSetsAndPreservesNullableHiddenDatesLinksAndOwners(bool? hidden)
    {
        var tables = Tables();
        tables[0].Rows[0][3] = DBNull.Value;
        tables[0].Rows[0][4] = hidden.HasValue ? hidden.Value : DBNull.Value;
        tables[1].Rows[0][1] = DBNull.Value;
        using var reader = new DataTableReader(tables);
        var snapshot = await SqlCatalogSnapshotProvider.ReadSnapshotAsync(reader, 6000, default);
        var item = Assert.Single(snapshot.Catalog.Values);
        Assert.Null(item.ModifiedDate);
        Assert.Null(item.LinkSourceId);
        Assert.Equal(hidden, item.Hidden);
        Assert.Null(Assert.Single(snapshot.DataSources.Values).ItemId);
        Assert.Null(Assert.Single(snapshot.DataSources.Values).Link);
        Assert.Equal(MonitorHarness.Fingerprint("synthetic source configuration"), Assert.Single(snapshot.DataSources.Values).Fingerprint);
    }

    [Fact]
    public async Task ProductionReaderAcceptsTwoEmptyCompleteSets()
    {
        var tables = Tables();
        foreach (var table in tables) { table.Rows.Clear(); }
        using var reader = new DataTableReader(tables);
        var snapshot = await SqlCatalogSnapshotProvider.ReadSnapshotAsync(reader, 6000, default);
        Assert.Empty(snapshot.Catalog);
        Assert.Empty(snapshot.DataSources);
    }

    [Theory]
    [InlineData("shortSourceHash")]
    [InlineData("longSourceHash")]
    [InlineData("nullSourceHash")]
    [InlineData("missingSources")]
    [InlineData("missingCatalog")]
    [InlineData("extraResultSet")]
    [InlineData("wrongCatalogShape")]
    [InlineData("wrongSourceShape")]
    [InlineData("wrongCatalogType")]
    [InlineData("wrongSourceType")]
    [InlineData("wrongCatalogName")]
    [InlineData("wrongSourceName")]
    [InlineData("swappedCatalogColumns")]
    [InlineData("swappedSourceColumns")]
    [InlineData("duplicateCatalogId")]
    [InlineData("duplicateSourceId")]
    [InlineData("nullCatalogId")]
    [InlineData("nullPath")]
    [InlineData("nullType")]
    [InlineData("nullSourceId")]
    [InlineData("emptyCatalogId")]
    [InlineData("emptySourceId")]
    [InlineData("oversizedPath")]
    public async Task ProductionReaderRejectsIncompleteMalformedOrAmbiguousSnapshots(string fault)
    {
        var tables = Tables();
        switch (fault)
        {
            case "shortSourceHash": tables[1].Rows[0][3] = new byte[31]; break;
            case "longSourceHash": tables[1].Rows[0][3] = new byte[33]; break;
            case "nullSourceHash": tables[1].Rows[0][3] = DBNull.Value; break;
            case "missingSources": tables = tables.Take(1).ToArray(); break;
            case "missingCatalog": tables = tables.Skip(1).ToArray(); break;
            case "extraResultSet": tables = [.. tables, new DataTable()]; break;
            case "wrongCatalogShape": tables[0].Columns.Add("unexpected", typeof(string)); break;
            case "wrongSourceShape": tables[1].Columns.Add("unexpected", typeof(string)); break;
            case "wrongCatalogType": tables[0] = tables[0].Clone(); tables[0].Columns[4].DataType = typeof(int); break;
            case "wrongSourceType": tables[1] = tables[1].Clone(); tables[1].Columns[3].DataType = typeof(string); break;
            case "wrongCatalogName": tables[0].Columns[0].ColumnName = "unexpected"; break;
            case "wrongSourceName": tables[1].Columns[0].ColumnName = "unexpected"; break;
            case "swappedCatalogColumns": tables[0].Columns["LinkSourceID"]!.SetOrdinal(0); break;
            case "swappedSourceColumns": tables[1].Columns["ItemID"]!.SetOrdinal(0); break;
            case "duplicateCatalogId": tables[0].Rows.Add(tables[0].Rows[0].ItemArray); break;
            case "duplicateSourceId": tables[1].Rows.Add(tables[1].Rows[0].ItemArray); break;
            case "nullCatalogId": tables[0].Rows[0][0] = DBNull.Value; break;
            case "nullPath": tables[0].Rows[0][1] = DBNull.Value; break;
            case "nullType": tables[0].Rows[0][2] = DBNull.Value; break;
            case "nullSourceId": tables[1].Rows[0][0] = DBNull.Value; break;
            case "emptyCatalogId": tables[0].Rows[0][0] = Guid.Empty; break;
            case "emptySourceId": tables[1].Rows[0][0] = Guid.Empty; break;
            case "oversizedPath": tables[0].Rows[0][1] = "/" + new string('a', 512); break;
        }
        using var reader = new DataTableReader(tables);
        await Assert.ThrowsAsync<CatalogSnapshotException>(() => SqlCatalogSnapshotProvider.ReadSnapshotAsync(reader, 6000, default));
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("sources")]
    public async Task ProductionReaderRejectsTheLimitDetectionRowWithoutReturningAPartialSnapshot(string table)
    {
        var tables = Tables();
        var target = table == "catalog" ? tables[0] : tables[1];
        var additions = table == "catalog" ? 1 : 3;
        for (var i = 0; i < additions; i++)
        {
            var values = target.Rows[0].ItemArray;
            values[0] = new Guid(i + 1, 0, 0, new byte[8]);
            target.Rows.Add(values);
        }
        using var reader = new DataTableReader(tables);
        await Assert.ThrowsAsync<CatalogSnapshotException>(() => SqlCatalogSnapshotProvider.ReadSnapshotAsync(reader, 1, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    [InlineData(int.MaxValue)]
    public async Task ProductionReaderRejectsInvalidLimitsBeforeReading(int maximumItems)
    {
        using var reader = new DataTableReader(Tables());
        await Assert.ThrowsAsync<CatalogSnapshotException>(() => SqlCatalogSnapshotProvider.ReadSnapshotAsync(reader, maximumItems, default));
    }

    [Fact]
    public async Task ProductionReaderHonorsCancellationBeforePublishingSnapshot()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var reader = new DataTableReader(Tables());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SqlCatalogSnapshotProvider.ReadSnapshotAsync(reader, 6000, cancelled.Token));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("cancel")]
    [InlineData("truncated")]
    public async Task ProductionReaderRequiresFinalResultCompletionAndRejectsCancellationOrTruncationAfterAllRows(string outcome)
    {
        using var cancellation = new CancellationTokenSource();
        using var reader = new FinalResultGateReader(new DataTableReader(Tables()));
        var reading = SqlCatalogSnapshotProvider.ReadSnapshotAsync(reader, 6000, cancellation.Token);
        await reader.ReachedFinalResult.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(reading.IsCompleted);
        if (outcome == "cancel")
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        }
        else if (outcome == "truncated")
        {
            reader.CompletionFailure = new IOException("Synthetic SQL stream ended before result completion.");
            reader.Release.TrySetResult();
            await Assert.ThrowsAsync<IOException>(() => reading);
        }
        else
        {
            reader.Release.TrySetResult();
            var snapshot = await reading;
            Assert.Single(snapshot.Catalog);
            Assert.Single(snapshot.DataSources);
        }
    }

    [Fact]
    public void EmbeddedQueryUsesOnlyTwoBoundedStaticSetsAgainstTheVerifiedQualifiedObjects()
    {
        var sql = EmbeddedSql();
        var executable = WithoutComments(sql);
        Assert.Equal(2, Regex.Matches(executable, @"\bSELECT\b", RegexOptions.IgnoreCase).Count);
        Assert.Contains("SELECT TOP (@MaximumCatalogRows + 1)", executable);
        Assert.Contains("SELECT TOP (@MaximumDataSourceRows + 1)", executable);
        Assert.Contains("[ItemID], [Path], [Type], [ModifiedDate], [Hidden], [LinkSourceID]", executable);
        Assert.Contains("[DSID], [ItemID], [Link],", executable);
        Assert.Contains("FROM [ReportServer].[dbo].[Catalog]", executable);
        Assert.Contains("FROM [ReportServer].[dbo].[DataSource]", executable);
        Assert.Contains("ORDER BY [ItemID];", executable);
        Assert.Contains("ORDER BY [DSID];", executable);
        Assert.Contains("SET NOCOUNT ON;", executable);
        Assert.Contains("SET XACT_ABORT ON;", executable);
        Assert.DoesNotContain("SELECT *", executable, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[] { "sys.", "sp_executesql", "EXEC ", "OBJECT_ID", "SERVERPROPERTY", "SchemaFingerprint", "DataSourceID", "[Content]", "USE ", "NOLOCK" })
        {
            Assert.DoesNotContain(forbidden, executable, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("SchemaFingerprint", typeof(CatalogSnapshot).GetProperties().Select(p => p.Name));
        Assert.Equal(3, Assert.Single(typeof(CatalogSnapshot).GetConstructors()).GetParameters().Length);
    }

    [Fact]
    public void EmbeddedQueryHashesEveryExplicitConfigurationFieldWithNullFramesAndMaxConversions()
    {
        var executable = WithoutComments(EmbeddedSql());
        string[] fields = ["SubscriptionID", "Name", "Extension", "CredentialRetrieval", "Prompt", "ConnectionString", "OriginalConnectionString",
            "OriginalConnectStringExpressionBased", "UserName", "Password", "Flags", "Version", "DSIDNum"];
        var framedFields = Regex.Matches(executable, @"CASE WHEN \[([^\]]+)\] IS NULL THEN 0x00 ELSE 0x01\s*\+ HASHBYTES\('SHA2_256', (.+?) END")
            .Cast<Match>().ToArray();
        Assert.Equal(fields, framedFields.Select(match => match.Groups[1].Value));
        Assert.Equal(fields.Length + 1, Regex.Matches(executable, @"HASHBYTES\('SHA2_256'").Count);
        Assert.Contains("HASHBYTES('SHA2_256', CONVERT(varbinary(max), 0x)", executable);
        foreach (var frame in framedFields)
        {
            var name = frame.Groups[1].Value;
            var input = frame.Groups[2].Value;
            Assert.Contains("CONVERT(varbinary(max),", input);
            Assert.Equal(name is "Name" or "Extension" or "Prompt"
                ? $"CONVERT(varbinary(max), CONVERT(nvarchar(max), [{name}])))"
                : $"CONVERT(varbinary(max), [{name}]))", input);
        }
        // Only relationship identifiers and the server-side hash occur outside the hash expression.
        var sourceSelect = executable[(executable.IndexOf("SELECT TOP (@MaximumDataSourceRows", StringComparison.Ordinal))..];
        Assert.Matches(@"^SELECT TOP \(@MaximumDataSourceRows \+ 1\)\s*\[DSID\], \[ItemID\], \[Link\],\s*HASHBYTES", sourceSelect);
        Assert.EndsWith(") AS [Fingerprint]\nFROM [ReportServer].[dbo].[DataSource]\nORDER BY [DSID];\n", sourceSelect.Replace("\r\n", "\n"));
    }

    private static string EmbeddedSql()
    {
        using var stream = typeof(SqlCatalogSnapshotProvider).Assembly.GetManifestResourceStream("SsrsPortal.ReadSsrsCatalogSnapshot.sql");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string WithoutComments(string sql) => Regex.Replace(sql, @"/\*.*?\*/|--[^\r\n]*", "", RegexOptions.Singleline);

    // Hold the final NextResultAsync after both sets' rows have been read. The gate
    // proves complete-read/cancellation ordering without timing-dependent sleeps.
    private sealed class FinalResultGateReader(DbDataReader inner) : DbDataReader
    {
        public TaskCompletionSource ReachedFinalResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? CompletionFailure { get; set; }
        private int _transitions;
        public override async Task<bool> NextResultAsync(CancellationToken ct)
        {
            if (++_transitions == 2)
            {
                ReachedFinalResult.TrySetResult();
                await Release.Task.WaitAsync(ct);
                if (CompletionFailure != null) { throw CompletionFailure; }
            }
            return await inner.NextResultAsync(ct);
        }
        public override Task<bool> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, offset, buffer, bufferOffset, length);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override bool NextResult() => inner.NextResult();
        public override bool Read() => inner.Read();
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); } base.Dispose(disposing); }
    }
}
