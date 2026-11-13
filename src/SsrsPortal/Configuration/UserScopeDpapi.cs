using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Options;

namespace SsrsPortal.Configuration;

// Framework DPAPI XML does not identify CurrentUser versus LocalMachine. The
// application envelope records its fixed producer policy; unmarked legacy rings
// require explicit infrastructure review/migration rather than silent adoption.
internal sealed class UserScopeDpapiOptions(ILoggerFactory loggerFactory) : IConfigureOptions<KeyManagementOptions>
{
    public void Configure(KeyManagementOptions options)
    {
        if (!OperatingSystem.IsWindows()) { throw new InvalidOperationException("User-scope DPAPI requires Windows."); }
        options.XmlEncryptor = new UserScopeDpapiEncryptor(loggerFactory);
    }
}

internal sealed class UserScopeDpapiEncryptor(ILoggerFactory loggerFactory) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        if (!OperatingSystem.IsWindows()) { throw new InvalidOperationException("User-scope DPAPI requires Windows."); }
        var encrypted = new DpapiXmlEncryptor(false, loggerFactory).Encrypt(plaintextElement);
        return new(new XElement("ssrs-user-dpapi", new XAttribute("version", "1"), encrypted.EncryptedElement), typeof(UserScopeDpapiDecryptor));
    }
}

public sealed class UserScopeDpapiDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        if (!OperatingSystem.IsWindows() || encryptedElement.Name != "ssrs-user-dpapi" ||
            (string?)encryptedElement.Attribute("version") != "1" || encryptedElement.Elements().Count() != 1 ||
            encryptedElement.Element("encryptedKey") is not { } inner)
        { throw new InvalidOperationException("Unsupported persistent key protection."); }
        return new DpapiXmlDecryptor(services).Decrypt(inner);
    }
}
