using Microsoft.VisualStudio.TestTools.UnitTesting;
using SsrsPortal.Subscriptions;

namespace SsrsPortal.SubscriptionTests;

[TestClass]
public sealed class SubscriptionConfigurationTests
{
    [TestMethod]
    [DataRow("Encrypt=Optional")]
    [DataRow("Encrypt=false")]
    [DataRow("Integrated Security=false;User ID=sensitive-user;Password=sensitive-password")]
    [DataRow("User ID=sensitive-user")]
    [DataRow("Password=sensitive-password")]
    [DataRow("Authentication=Active Directory Integrated;Integrated Security=false")]
    [DataRow("AttachDBFilename=C:\\sensitive-path\\private.mdf")]
    [DataRow("User Instance=true")]
    [DataRow("Persist Security Info=true")]
    [DataRow("Initial Catalog=master")]
    public void RuntimeSqlRejectsUnsafeSettingsWithoutEchoingCredentials(string setting)
    {
        var options = SubscriptionTests.Options;
        options.ConnectionString += ";" + setting;
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.IsNull(error.InnerException);
        Assert.IsFalse(error.Message.Contains("sensitive-user"));
        Assert.IsFalse(error.Message.Contains("sensitive-password"));
        Assert.IsFalse(error.Message.Contains("sensitive-path"));
    }
    [TestMethod]
    [DataRow("true", true)]
    [DataRow("true", false)]
    [DataRow("Mandatory", true)]
    [DataRow("Mandatory", false)]
    [DataRow("Strict", true)]
    [DataRow("Strict", false)]
    public void RuntimeSqlAcceptsIntegratedIdentityWithEncryptedTls(string encryption, bool trustServerCertificate)
    {
        var options = SubscriptionTests.Options;
        options.ConnectionString = $"Server=sql.example.invalid;Database=SsrsWeb;Integrated Security=true;Encrypt={encryption};TrustServerCertificate={trustServerCertificate}";
        options.Validate(); // Configuration validation performs no network or secret lookup.
    }
    [TestMethod]
    public void DisabledSubscriptionsNeedNoExternalInfrastructure() => new SubscriptionOptions().Validate();
}
