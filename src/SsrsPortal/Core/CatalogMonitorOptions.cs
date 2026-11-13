using Microsoft.Data.SqlClient;

namespace SsrsPortal.Core;

public sealed class CatalogMonitorOptions
{
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; } = 30;
    public string ConnectionString { get; set; } = "";
    public int CommandTimeoutSeconds { get; set; } = 5;

    public void Validate()
    {
        if (IntervalSeconds is < 5 or > 3600 || CommandTimeoutSeconds is < 1 or > 120)
        {
            throw new InvalidOperationException("CatalogMonitor intervals must be 5–3600 seconds and timeouts 1–120 seconds.");
        }
        if (Enabled)
        {
            _ = ValidatedConnectionString();
        }
    }

    public string ValidatedConnectionString()
    {
        try
        {
            var sql = new SqlConnectionStringBuilder(ConnectionString);
            if (string.IsNullOrWhiteSpace(sql.DataSource) || string.IsNullOrWhiteSpace(sql.InitialCatalog) ||
                !sql.IntegratedSecurity || sql.Authentication != SqlAuthenticationMethod.NotSpecified ||
                sql.UserID.Length != 0 || sql.Password.Length != 0 || sql.TrustServerCertificate ||
                (sql.Encrypt != SqlConnectionEncryptOption.Mandatory && sql.Encrypt != SqlConnectionEncryptOption.Strict) ||
                sql.AttachDBFilename.Length != 0 || sql.UserInstance || sql.PersistSecurityInfo)
            {
                throw new ArgumentException();
            }
            sql.ApplicationIntent = ApplicationIntent.ReadOnly;
            sql.ConnectRetryCount = 0;
            sql.ConnectTimeout = sql.ConnectTimeout == 0 ? CommandTimeoutSeconds : Math.Min(sql.ConnectTimeout, CommandTimeoutSeconds);
            sql.MultipleActiveResultSets = false;
            return sql.ConnectionString;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            // SqlClient parsing errors can quote the submitted connection string. Never retain an
            // inner exception or interpolate a configuration value into diagnostics.
            throw new InvalidOperationException("CatalogMonitor requires a server/database, Windows integrated authentication, and validated TLS (Encrypt=Mandatory or Strict; TrustServerCertificate=False), without SQL credentials.");
        }
    }
}
