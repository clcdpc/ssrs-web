using System.Xml;
using System.Xml.Linq;
using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Options;

namespace SsrsPortal.Configuration;

internal static class PersistentKeys
{
    private const string ProtectionNamespace = "http://schemas.asp.net/2015/03/dataProtection";
    private const int MaximumFiles = 1024;
    private const long MaximumFileBytes = 1024 * 1024;
    private const long MaximumTotalBytes = 16 * 1024 * 1024;
    private const string InvalidRingMessage = "Persistent key directory contains invalid, unsupported or unreadable retained key metadata.";

    internal static void ValidateDirectory(string root, string applicationRoot, Func<string, DriveType>? driveClassifier = null)
    {
        if (!OperatingSystem.IsWindows()) { throw new InvalidOperationException("Deployed persistent keys require Windows user-scope DPAPI."); }

        LocalFileSystemPolicy.RequireLocalPhysicalPath(root, "Key directory", driveClassifier);
        LocalFileSystemPolicy.RequireLocalPhysicalPath(applicationRoot, "Application directory", driveClassifier);
        var directory = new DirectoryInfo(root);
        if (!directory.Exists || SameOrWithin(root, applicationRoot) || SameOrWithin(applicationRoot, root))
        { throw new InvalidOperationException("The provisioned key directory must be outside application content."); }

        var before = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var keyIds = new HashSet<Guid>();
        var count = 0;
        long total = 0;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (++count > MaximumFiles || entry is not FileInfo file || file.Extension != ".xml" ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint) || file.Length > MaximumFileBytes)
            { throw InvalidRing(); }
            if ((total += file.Length) > MaximumTotalBytes) { throw InvalidRing(); }

            byte[] bytes;
            try
            {
                using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaximumFileBytes) { throw InvalidRing(); }
                using var buffer = new MemoryStream(checked((int)stream.Length));
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            catch (InvalidOperationException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { throw InvalidRing(); }

            XDocument document;
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                using var reader = XmlReader.Create(input, new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumFileBytes });
                document = XDocument.Load(reader, LoadOptions.None);
            }
            catch (Exception exception) when (exception is XmlException or InvalidOperationException or ArgumentException)
            { throw InvalidRing(); }

            var xmlRoot = document.Root;
            if (xmlRoot is null) { throw InvalidRing(); }
            if (xmlRoot.Name == "key")
            {
                var id = ValidateKeyDocument(xmlRoot);
                if (!keyIds.Add(id)) { throw InvalidRing(); }
            }
            else if (xmlRoot.Name == "revocation") { ValidateRevocationDocument(xmlRoot); }
            else { throw InvalidRing(); }
            before.Add(file.FullName, bytes);
        }

        // XmlKeyManager intentionally skips some malformed XML files. Compare its full
        // view with our strictly parsed IDs before startup can Protect and generate a key.
        IReadOnlyCollection<IKey> frameworkKeys;
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            Configure(services, root);
            using var provider = services.BuildServiceProvider();
            frameworkKeys = provider.GetRequiredService<IKeyManager>().GetAllKeys().ToArray();
            var frameworkIds = frameworkKeys.Select(key => key.KeyId).ToArray();
            if (frameworkIds.Length != keyIds.Count || frameworkIds.Distinct().Count() != frameworkIds.Length ||
                frameworkIds.Any(id => !keyIds.Contains(id)))
            { throw InvalidRing(); }
        }
        catch (InvalidOperationException exception) when (exception.Message == InvalidRingMessage) { throw; }
        catch (Exception) { throw InvalidRing(); }

        // The framework manager is expected to read only. Confirm the inventory and
        // exact bytes remained unchanged even if a retained key could not be loaded.
        var afterEntries = directory.EnumerateFileSystemInfos().ToArray();
        if (afterEntries.Length != before.Count || afterEntries.Any(entry => entry is not FileInfo file || !before.ContainsKey(file.FullName)))
        { throw InvalidRing(); }
        foreach (var file in before)
        {
            byte[] after;
            try { after = File.ReadAllBytes(file.Key); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { throw InvalidRing(); }
            if (!after.AsSpan().SequenceEqual(file.Value)) { throw InvalidRing(); }
        }
    }

    private static Guid ValidateKeyDocument(XElement root)
    {
        if (!AttributesAre(root, "id", "version") || (string?)root.Attribute("version") != "1" ||
            !Guid.TryParseExact((string?)root.Attribute("id"), "D", out var id) || id == Guid.Empty)
        { throw InvalidRing(); }
        var elements = root.Elements().ToArray();
        if (elements.Length != 4 || elements[0].Name != "creationDate" || elements[1].Name != "activationDate" ||
            elements[2].Name != "expirationDate" || elements[3].Name != "descriptor")
        { throw InvalidRing(); }
        _ = ParseXmlDate(elements[0]);
        var activated = ParseXmlDate(elements[1]);
        var expires = ParseXmlDate(elements[2]);
        if (activated >= expires) { throw InvalidRing(); }

        var descriptor = elements[3];
        var descriptorType = typeof(IKeyManager).Assembly.GetType(
            "Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.AuthenticatedEncryptorDescriptorDeserializer");
        if (descriptorType is null || !AttributesAre(descriptor, "deserializerType") ||
            !MatchesTrustedTypeIdentity((string?)descriptor.Attribute("deserializerType"), descriptorType) ||
            descriptor.Elements().Count() != 1 || descriptor.Element("descriptor") is not { } data)
        { throw InvalidRing(); }

        var dataElements = data.Elements().ToArray();
        if (dataElements.Length != 3 || dataElements[0].Name != "encryption" || dataElements[1].Name != "validation" ||
            dataElements[2].Name != XName.Get("encryptedSecret", ProtectionNamespace) ||
            data.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration) ||
            !AttributesAre(dataElements[0], "algorithm") || (string?)dataElements[0].Attribute("algorithm") != "AES_256_CBC" ||
            !AttributesAre(dataElements[1], "algorithm") || (string?)dataElements[1].Attribute("algorithm") != "HMACSHA256")
        { throw InvalidRing(); }

        var encrypted = dataElements[2];
        if (!AttributesAre(encrypted, "decryptorType") ||
            !MatchesBindableAppDecryptorIdentity((string?)encrypted.Attribute("decryptorType")) ||
            encrypted.Elements().Count() != 1 || encrypted.Element("ssrs-user-dpapi") is not { } envelope ||
            !AttributesAre(envelope, "version") || (string?)envelope.Attribute("version") != "1" ||
            envelope.Elements().Count() != 1 || envelope.Element("encryptedKey") is not { } encryptedKey ||
            encryptedKey.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration) ||
            encryptedKey.Elements().Count() != 1 || encryptedKey.Element("value") is not { } value ||
            !AttributesAre(value) || value.HasElements || !HasNonemptyBase64Payload(value.Value))
        { throw InvalidRing(); }
        return id;
    }

    internal static bool MatchesTrustedTypeIdentity(string? submitted, Type trustedType)
        => TryParseTrustedTypeIdentity(submitted, trustedType, out _);

    internal static bool MatchesBindableAppDecryptorIdentity(string? submitted)
    {
        if (!TryParseTrustedTypeIdentity(submitted, typeof(UserScopeDpapiDecryptor), out var requestedVersion)) { return false; }
        var loadedVersion = typeof(UserScopeDpapiDecryptor).Assembly.GetName().Version;
        return loadedVersion is not null && NormalizeAssemblyVersion(requestedVersion).CompareTo(NormalizeAssemblyVersion(loadedVersion)) <= 0;
    }

    private static bool TryParseTrustedTypeIdentity(string? submitted, Type trustedType, out Version parsedVersion)
    {
        parsedVersion = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(submitted) || submitted.Length > 1024 || trustedType.FullName is null) { return false; }
        var components = submitted.Split(',', StringSplitOptions.TrimEntries);
        if (components.Length != 5 || components[0] != trustedType.FullName) { return false; }

        var expectedAssembly = trustedType.Assembly.GetName();
        if (!string.Equals(components[1], expectedAssembly.Name, StringComparison.OrdinalIgnoreCase)) { return false; }
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 2; index < components.Length; index++)
        {
            var separator = components[index].IndexOf('=');
            if (separator <= 0 || separator == components[index].Length - 1) { return false; }
            var name = components[index][..separator].Trim();
            var value = components[index][(separator + 1)..].Trim();
            if (!(name.Equals("Version", StringComparison.OrdinalIgnoreCase) || name.Equals("Culture", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("PublicKeyToken", StringComparison.OrdinalIgnoreCase)) || !fields.TryAdd(name, value)) { return false; }
        }

        if (fields.Count != 3 || !fields.TryGetValue("Version", out var versionValue) ||
            !TryParseAssemblyVersion(versionValue, out parsedVersion) ||
            !fields.TryGetValue("Culture", out var culture) ||
            !fields.TryGetValue("PublicKeyToken", out var publicKeyToken))
        { return false; }

        var expectedCulture = string.IsNullOrEmpty(expectedAssembly.CultureName) ? "neutral" : expectedAssembly.CultureName;
        var token = expectedAssembly.GetPublicKeyToken();
        var expectedPublicKeyToken = token is { Length: > 0 } ? Convert.ToHexString(token) : "null";
        return string.Equals(culture, expectedCulture, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(publicKeyToken, expectedPublicKeyToken, StringComparison.OrdinalIgnoreCase);
    }

    private static Version NormalizeAssemblyVersion(Version version) => new(version.Major, version.Minor,
        version.Build < 0 ? 0 : version.Build, version.Revision < 0 ? 0 : version.Revision);

    private static bool TryParseAssemblyVersion(string value, out Version version)
    {
        version = new Version(0, 0);
        var parts = value.Split('.');
        if (parts.Length is >= 2 and <= 4 &&
            parts.All(part => part.Length > 0 && part.All(char.IsAsciiDigit) &&
                ushort.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)) &&
            Version.TryParse(value, out var parsed) && parsed.Major >= 0)
        {
            version = parsed;
            return true;
        }
        return false;
    }

    private static bool HasNonemptyBase64Payload(string value)
    {
        var decoded = new byte[checked(((value.Length + 3) / 4) * 3)];
        return Convert.TryFromBase64String(value, decoded, out var bytesWritten) && bytesWritten > 0;
    }

    private static void ValidateRevocationDocument(XElement root)
    {
        if (!AttributesAre(root, "version") || (string?)root.Attribute("version") != "1") { throw InvalidRing(); }
        var elements = root.Elements().ToArray();
        if (elements.Length != 3 || elements[0].Name != "revocationDate" || elements[1].Name != "key" || elements[2].Name != "reason")
        { throw InvalidRing(); }
        _ = ParseXmlDate(elements[0]);
        if (!AttributesAre(elements[1], "id")) { throw InvalidRing(); }
        var target = (string?)elements[1].Attribute("id");
        if (target != "*" && (!Guid.TryParseExact(target, "D", out var id) || id == Guid.Empty)) { throw InvalidRing(); }
        if (elements[1].HasElements || !elements[1].IsEmpty || elements[2].HasElements ||
            elements[2].Attributes().Any(attribute => !attribute.IsNamespaceDeclaration))
        { throw InvalidRing(); }
    }

    private static DateTimeOffset ParseXmlDate(XElement element)
    {
        if (!AttributesAre(element) || element.HasElements || string.IsNullOrWhiteSpace(element.Value)) { throw InvalidRing(); }
        try { return XmlConvert.ToDateTimeOffset(element.Value); }
        catch (FormatException) { throw InvalidRing(); }
    }

    private static bool AttributesAre(XElement element, params string[] names)
    {
        var attributes = element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration).ToArray();
        return attributes.Length == names.Length && names.All(name => attributes.Count(attribute => attribute.Name == name) == 1);
    }

    private static InvalidOperationException InvalidRing() =>
        new(InvalidRingMessage);

    private static bool SameOrWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
            (fullPath + Path.DirectorySeparatorChar).StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Register(WebApplicationBuilder builder, ApplicationConfiguration settings)
    {
        if (builder.Environment.IsDevelopment()) { return; }
        ValidateDirectory(settings.Keys.KeyRingPath, builder.Environment.ContentRootPath);
        if (OperatingSystem.IsWindows()) { Configure(builder.Services, settings.Keys.KeyRingPath); }
    }

    internal static void Configure(IServiceCollection services, string root)
    {
        services.AddDataProtection().SetApplicationName(PersistentKeyOptions.ApplicationName).PersistKeysToFileSystem(new(root));
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>, UserScopeDpapiOptions>();
    }

    internal static void VerifyUsable(IServiceProvider services, string root, string applicationRoot)
    {
        ValidateDirectory(root, applicationRoot);
        // Fail if any retained usable key cannot decrypt; never mask a wrong account/profile by creating replacements.
        foreach (var key in services.GetRequiredService<IKeyManager>().GetAllKeys().Where(key => !key.IsRevoked))
        {
            try { _ = key.CreateEncryptor() ?? throw new InvalidOperationException("Retained key cannot be used."); }
            catch (InvalidOperationException exception) when (exception.Message == "Retained key cannot be used.") { throw; }
            catch (Exception) { throw new InvalidOperationException("Retained key cannot be used by the application process identity."); }
        }
        var protector = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("clc-readiness-v1");
        const string value = "local-startup-smoke";
        if (protector.Unprotect(protector.Protect(value)) != value) { throw new InvalidOperationException("Persistent key smoke failed."); }
    }
}
