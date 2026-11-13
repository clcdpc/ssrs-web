using System.Security.Cryptography;
using System.Text;
using SsrsPortal.Services;

namespace SsrsPortal.Tests;

// These tests exercise the byte-framing design with synthetic inputs. They do not
// execute T-SQL or establish compatibility/performance against a live ReportServer.
public sealed class CatalogMonitorFingerprintTests
{
    private static readonly string[] Fields = ["SubscriptionID", "Name", "Extension", "CredentialRetrieval", "Prompt", "ConnectionString",
        "OriginalConnectionString", "OriginalConnectStringExpressionBased", "UserName", "Password", "Flags", "Version", "DSIDNum"];

    [Theory]
    [InlineData("SubscriptionID")]
    [InlineData("Name")]
    [InlineData("Extension")]
    [InlineData("CredentialRetrieval")]
    [InlineData("Prompt")]
    [InlineData("ConnectionString")]
    [InlineData("OriginalConnectionString")]
    [InlineData("OriginalConnectStringExpressionBased")]
    [InlineData("UserName")]
    [InlineData("Password")]
    [InlineData("Flags")]
    [InlineData("Version")]
    [InlineData("DSIDNum")]
    public void EveryKnownConfigurationFieldChangesTheFingerprintAndTriggersCatalogRefresh(string field)
    {
        var values = Fields.Select(Text).ToArray();
        var original = Fingerprint(values);
        values[Array.IndexOf(Fields, field)] = Text("changed synthetic configuration");
        var changed = Fingerprint(values);
        Assert.NotEqual(original, changed);
        var item = new SqlCatalogItem(Guid.Parse("8618808d-54cd-4b15-b41e-9f2db8b07444"), MonitorHarness.A, 2, new DateTime(2026, 10, 10), null);
        var source = new SqlDataSource(Guid.Parse("b7c25b56-248b-450d-ad05-91a88182161b"), item.ItemId, null, original);
        var previous = new CatalogSnapshot([item], [source], 10);
        var current = new CatalogSnapshot([item], [source with { Fingerprint = changed }], 10);
        var change = current.ChangesFrom(previous);
        Assert.Equal(1, change.DataSourcesChanged);
        Assert.Equal(0, change.CatalogItemsChanged);
        Assert.True(change.Changed);
        Assert.False(change.ForceRefresh);
    }

    [Fact]
    public void NullEmptyAndDifferentFieldPositionsHaveDistinctFrames()
    {
        byte[]?[][] examples = [[null, null], [[], null], [null, []], [[], []], [Text("a"), null], [null, Text("a")]];
        Assert.Equal(examples.Length, examples.Select(Fingerprint).Distinct(StringComparer.Ordinal).Count());
        Assert.NotEqual(Fingerprint([Text("ab"), Text("c")]), Fingerprint([Text("a"), Text("bc")]));
        Assert.NotEqual(Fingerprint([Text("a"), Text("b")]), Fingerprint([Text("b"), Text("a")]));
        // Sentinel-like bytes inside a value cannot be mistaken for field frames.
        Assert.NotEqual(Fingerprint([[0x00, 0x01], []]), Fingerprint([[0x00], [0x01]]));
    }

    [Theory]
    [InlineData("Prompt")]
    [InlineData("ConnectionString")]
    [InlineData("OriginalConnectionString")]
    [InlineData("UserName")]
    [InlineData("Password")]
    public void LargeTextAndLegacyBinaryInputsKeepChangesBeyondEightThousandBytes(string field)
    {
        var values = new byte[]?[Fields.Length];
        var position = Array.IndexOf(Fields, field);
        var large = field == "Prompt" ? Text(new string('\u03bb', 12000) + "A") : Enumerable.Repeat((byte)0x31, 24001).ToArray();
        Assert.True(large.Length > 8000);
        values[position] = large;
        var original = Fingerprint(values);
        var changed = (byte[])large.Clone();
        changed[^1] ^= 0x01;
        values[position] = changed;
        Assert.NotEqual(original, Fingerprint(values));
        values[position] = large[..8000];
        Assert.NotEqual(original, Fingerprint(values));
    }

    [Fact]
    public void SyntheticFingerprintIsDeterministicAndHasTheExpectedSha256Width()
    {
        byte[]?[] values = [null, [], Text("synthetic field"), [0x00, 0xff]];
        var fingerprint = Fingerprint(values);
        Assert.Equal(64, fingerprint.Length);
        Assert.Equal(fingerprint, Fingerprint(values.Select(value => value?.ToArray()).ToArray()));
    }

    private static byte[] Text(string text) => Encoding.Unicode.GetBytes(text);

    private static string Fingerprint(byte[]?[] fields)
    {
        using var outer = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var field in fields)
        {
            outer.AppendData(field == null ? [0x00] : [0x01]);
            if (field != null) { outer.AppendData(SHA256.HashData(field)); }
        }
        return Convert.ToHexString(outer.GetHashAndReset());
    }
}
