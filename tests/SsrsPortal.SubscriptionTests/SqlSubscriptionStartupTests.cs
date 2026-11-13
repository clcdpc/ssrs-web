using System.Text.RegularExpressions;
using Dapper;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.SubscriptionTests;

[TestClass]
public sealed class SqlSubscriptionStartupTests
{
    private const string Admin = "Server=localhost;Database=master;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;Connect Timeout=2;Pooling=false";
    private string database = "";
    private string connectionString = "";

    [ClassInitialize]
    public static async Task Available(TestContext context)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Real SQL startup qualification requires a local Windows SQL Server administrator."); }
        await using var db = new SqlConnection(Admin);
        try { await db.OpenAsync(); }
        catch (SqlException) { Assert.Inconclusive("Local SQL Server unavailable; run on the SQL qualification host."); }
        if (await db.ExecuteScalarAsync<int>("SELECT IS_SRVROLEMEMBER('sysadmin');") != 1)
        { Assert.Inconclusive("SQL startup fixture requires isolated database create/drop permission."); }
    }

    [TestInitialize]
    public async Task CreateDatabase()
    {
        database = "SsrsWeb_StartupTests_" + Guid.NewGuid().ToString("N");
        await using var db = new SqlConnection(Admin);
        await db.ExecuteAsync($"CREATE DATABASE [{database}];");
        connectionString = new SqlConnectionStringBuilder(Admin) { InitialCatalog = database }.ConnectionString;
    }

    [TestCleanup]
    public async Task DropDatabase()
    {
        Hangfire.Logging.LogProvider.SetCurrentLogProvider(null);
        if (database.Length == 0) { return; }
        if (!Regex.IsMatch(database, @"^SsrsWeb_StartupTests_[a-f0-9]{32}$")) { throw new InvalidOperationException("Unexpected test database."); }
        SqlConnection.ClearAllPools();
        await using var db = new SqlConnection(Admin);
        await db.ExecuteAsync($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
    }

    private async Task Apply(string filename)
    {
        await using var db = new SqlConnection(connectionString);
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, filename));
        foreach (var batch in Regex.Split(script, @"^GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(batch)) { await db.ExecuteAsync(batch); }
        }
    }

    [TestMethod]
    public async Task DevelopmentInitializesAndRepeatedStartupPreservesDataWithoutDdl()
    {
        await Apply("001-Subscriptions.sql");
        await using (var app = SubscriptionStartupTests.Host(Environments.Development, connectionString))
        {
            await app.StartAsync();
            await using var db = new SqlConnection(connectionString);
            Assert.AreEqual(9, await db.ExecuteScalarAsync<int>("SELECT [Version] FROM [HangFire].[Schema];"));
            await db.ExecuteAsync("INSERT [HangFire].[Hash] ([Key],[Field],[Value]) VALUES ('startup-test','sentinel','preserved');");
            await app.StopAsync();
        }
        // Reject DDL and even the version UPDATE from an otherwise idempotent
        // installer invocation, proving that an initialized restart skips it.
        await using (var db = new SqlConnection(connectionString))
        {
            await db.ExecuteAsync("""
                CREATE TRIGGER [HangFire].[RejectStartupSchemaWrites] ON [HangFire].[Schema] AFTER INSERT, UPDATE, DELETE
                AS THROW 51011, 'Unexpected schema version write.', 1;
                """);
            await db.ExecuteAsync("""
                CREATE TRIGGER [RejectStartupDdl] ON DATABASE FOR DDL_DATABASE_LEVEL_EVENTS
                AS THROW 51010, 'Unexpected runtime DDL.', 1;
                """);
        }
        await using (var app = SubscriptionStartupTests.Host(Environments.Development, connectionString))
        {
            await app.StartAsync();
            await using var db = new SqlConnection(connectionString);
            Assert.AreEqual("preserved", await db.ExecuteScalarAsync<string>("SELECT [Value] FROM [HangFire].[Hash] WHERE [Key]='startup-test' AND [Field]='sentinel';"));
            await app.StopAsync();
        }
    }

    [TestMethod]
    [DataRow("Development")]
    [DataRow("Production")]
    public async Task MissingApplicationSchemaFailsBeforeHangfireOrWorkers(string environment)
    {
        await using var app = SubscriptionStartupTests.Host(environment, connectionString);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        StringAssert.Contains(error.Message, "[subscriptions].[SchemaVersion]");
        StringAssert.Contains(error.Message, "expected schema version 1");
        StringAssert.Contains(error.Message, "observed missing");
        Assert.IsInstanceOfType<SqlException>(error.InnerException);
        Assert.IsFalse(app.Services.GetRequiredService<SubscriptionStartupTests.ReadinessProbe>().Started);
        await using var db = new SqlConnection(connectionString);
        Assert.AreEqual(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.schemas WHERE name='HangFire';"));
    }

    [TestMethod]
    [DataRow("Production")]
    [DataRow("Staging")]
    [DataRow("DevelopmentLike")]
    public async Task NonDevelopmentNeverCreatesMissingHangfire(string environment)
    {
        await Apply("001-Subscriptions.sql");
        await using var app = SubscriptionStartupTests.Host(environment, connectionString);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        StringAssert.Contains(error.Message, "[HangFire].[Schema]");
        StringAssert.Contains(error.Message, "expected schema version 9");
        StringAssert.Contains(error.Message, "observed missing");
        Assert.IsInstanceOfType<SqlException>(error.InnerException);
        Assert.IsFalse(app.Services.GetRequiredService<SubscriptionStartupTests.ReadinessProbe>().Started);
        await using var db = new SqlConnection(connectionString);
        Assert.AreEqual(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.schemas WHERE name='HangFire';"));
    }

    [TestMethod]
    [DataRow("Development", "subscriptions", 2)]
    [DataRow("Production", "subscriptions", 0)]
    [DataRow("Development", "HangFire", 8)]
    [DataRow("Development", "HangFire", 10)]
    [DataRow("Production", "HangFire", 8)]
    [DataRow("Production", "HangFire", 10)]
    public async Task UnsupportedVersionsFailWithoutUpgrading(string environment, string schema, int observed)
    {
        await Apply("001-Subscriptions.sql");
        await Apply("002-Hangfire.sql");
        await using var db = new SqlConnection(connectionString);
        var table = schema == "HangFire" ? "Schema" : "SchemaVersion";
        await db.ExecuteAsync($"UPDATE [{schema}].[{table}] SET [Version]=@observed;", new { observed });
        await using var app = SubscriptionStartupTests.Host(environment, connectionString);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        StringAssert.Contains(error.Message, $"[{schema}]");
        StringAssert.Contains(error.Message, $"expected {(schema == "HangFire" ? 9 : 1)}, observed {observed}");
        Assert.IsFalse(app.Services.GetRequiredService<SubscriptionStartupTests.ReadinessProbe>().Started);
        Assert.AreEqual(observed, await db.ExecuteScalarAsync<int>($"SELECT [Version] FROM [{schema}].[{table}];"));
    }

    [TestMethod]
    public async Task DevelopmentInstallationPermissionFailurePreservesSqlError()
    {
        await Apply("001-Subscriptions.sql");
        await using var db = new SqlConnection(connectionString); await db.OpenAsync();
        await db.ExecuteAsync("CREATE USER [StartupRuntime] WITHOUT LOGIN; ALTER ROLE [SsrsSubscriptionRuntime] ADD MEMBER [StartupRuntime]; EXECUTE AS USER='StartupRuntime';");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => SubscriptionSchemaCheck.EnsureSchemas(db, development: true));
            StringAssert.Contains(error.Message, "Development could not initialize [HangFire]");
            StringAssert.Contains(error.Message, "CREATE SCHEMA");
            StringAssert.Contains(error.Message, "002-Hangfire.sql");
            Assert.IsInstanceOfType<SqlException>(error.InnerException);
        }
        finally { await db.ExecuteAsync("REVERT;"); }
        Assert.AreEqual(0, await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sys.schemas WHERE name='HangFire';"));
    }

    [TestMethod]
    public async Task DevelopmentInstallerNeedsOnlyExplicitLocalDdlGrants()
    {
        await Apply("001-Subscriptions.sql");
        await using var db = new SqlConnection(connectionString); await db.OpenAsync();
        await db.ExecuteAsync("CREATE USER [StartupDeveloper] WITHOUT LOGIN; ALTER ROLE [SsrsSubscriptionRuntime] ADD MEMBER [StartupDeveloper]; GRANT CREATE SCHEMA, CREATE TABLE TO [StartupDeveloper]; EXECUTE AS USER='StartupDeveloper';");
        try
        {
            SubscriptionSchemaCheck.EnsureSchemas(db, development: true);
            Assert.AreEqual(9, await db.ExecuteScalarAsync<int>("SELECT [Version] FROM [HangFire].[Schema];"));
        }
        finally { await db.ExecuteAsync("REVERT;"); }
    }

    [TestMethod]
    [DataRow("Production", "")]
    [DataRow("Development", "8,9")]
    public async Task MissingOrMultipleVersionRowsAreRejected(string environment, string values)
    {
        await Apply("001-Subscriptions.sql");
        await Apply("002-Hangfire.sql");
        await using var db = new SqlConnection(connectionString);
        await db.ExecuteAsync("DELETE FROM [HangFire].[Schema];");
        foreach (var version in values.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse))
        { await db.ExecuteAsync("INSERT [HangFire].[Schema] ([Version]) VALUES (@version);", new { version }); }
        await using var app = SubscriptionStartupTests.Host(environment, connectionString);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
        StringAssert.Contains(error.Message, "[HangFire] schema version mismatch: expected 9, observed");
        Assert.IsFalse(app.Services.GetRequiredService<SubscriptionStartupTests.ReadinessProbe>().Started);
    }

    [TestMethod]
    public async Task SchemaReadPermissionFailureIdentifiesObjectAndPreservesSqlError()
    {
        await Apply("001-Subscriptions.sql");
        await Apply("002-Hangfire.sql");
        await using var db = new SqlConnection(connectionString); await db.OpenAsync();
        await db.ExecuteAsync("CREATE USER [StartupRuntime] WITHOUT LOGIN; ALTER ROLE [SsrsSubscriptionRuntime] ADD MEMBER [StartupRuntime]; DENY SELECT ON [subscriptions].[SchemaVersion] TO [StartupRuntime]; EXECUTE AS USER='StartupRuntime';");
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => SubscriptionSchemaCheck.EnsureSchemas(db, development: true));
            StringAssert.Contains(error.Message, "cannot read [subscriptions].[SchemaVersion]");
            StringAssert.Contains(error.Message, "SELECT permission");
            Assert.AreEqual(229, ((SqlException)error.InnerException!).Number);
        }
        finally { await db.ExecuteAsync("REVERT;"); }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ManualMigrationsRepeatAndLeastPrivilegeIdentityNeedsNoDdl(bool development)
    {
        for (var repeat = 0; repeat < 2; repeat++)
        {
            await Apply("001-Subscriptions.sql");
            await Apply("002-Hangfire.sql");
            await Apply("003-SubscriptionPermissions.sql");
        }
        await using var db = new SqlConnection(connectionString); await db.OpenAsync();
        await db.ExecuteAsync("CREATE USER [StartupRuntime] WITHOUT LOGIN; ALTER ROLE [SsrsSubscriptionRuntime] ADD MEMBER [StartupRuntime]; EXECUTE AS USER='StartupRuntime';");
        try
        {
            SubscriptionSchemaCheck.EnsureSchemas(db, development);
            Assert.AreEqual(0, await db.ExecuteScalarAsync<int>("SELECT HAS_PERMS_BY_NAME(DB_NAME(),'DATABASE','CREATE TABLE');"));
            Assert.AreEqual(0, await db.ExecuteScalarAsync<int>("SELECT HAS_PERMS_BY_NAME('HangFire','SCHEMA','ALTER');"));
            Assert.AreEqual(1, await db.ExecuteScalarAsync<int>("SELECT HAS_PERMS_BY_NAME('HangFire.Job','OBJECT','INSERT');"));
        }
        finally { await db.ExecuteAsync("REVERT;"); }
    }

    [TestMethod]
    public async Task QueuedBackgroundJobWaitsForSuccessfulSchemaGate()
    {
        await Apply("001-Subscriptions.sql");
        await Apply("002-Hangfire.sql");
        var storage = new SqlServerStorage(connectionString, new SqlServerStorageOptions { PrepareSchemaIfNecessary = false, TryAutoDetectSchemaDependentOptions = false });
        var jobId = new BackgroundJobClient(storage).Enqueue(() => ReadyJob(connectionString));
        await using var db = new SqlConnection(connectionString);
        await db.ExecuteAsync("UPDATE [HangFire].[Schema] SET [Version]=10;");
        await using (var app = SubscriptionStartupTests.Host(Environments.Development, connectionString))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => app.StartAsync());
            Assert.AreEqual("Enqueued", await db.ExecuteScalarAsync<string>("SELECT [StateName] FROM [HangFire].[Job] WHERE [Id]=@jobId;", new { jobId }));
            Assert.IsFalse(app.Services.GetRequiredService<SubscriptionStartupTests.ReadinessProbe>().Started);
        }
        await db.ExecuteAsync("UPDATE [HangFire].[Schema] SET [Version]=9;");
        await using (var app = SubscriptionStartupTests.Host(Environments.Development, connectionString))
        {
            await app.StartAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (await db.ExecuteScalarAsync<string>("SELECT [StateName] FROM [HangFire].[Job] WHERE [Id]=@jobId;", new { jobId }) != "Succeeded")
            { await Task.Delay(50, deadline.Token); }
            await app.StopAsync();
        }
    }

    [Queue("subscriptions")]
    [AutomaticRetry(Attempts = 0)]
    public static void ReadyJob(string connectionString)
    {
        using var db = new SqlConnection(connectionString);
        Assert.AreEqual(1, db.ExecuteScalar<int>("SELECT [Version] FROM [subscriptions].[SchemaVersion] WHERE [Id]=1;"));
        Assert.AreEqual(9, db.ExecuteScalar<int>("SELECT [Version] FROM [HangFire].[Schema];"));
    }
}
