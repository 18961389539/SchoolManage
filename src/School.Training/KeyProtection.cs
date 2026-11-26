using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace School.Training;

/// <summary>
/// How DataProtection keys are sealed at rest. Windows DPAPI cannot be opened after the
/// system is reinstalled. A school certificate or a recovery passphrase replaces that seal
/// so a backup can be read on another machine that has the same secret.
/// </summary>
public sealed class KeyProtectionOptions
{
    public const int RecoveryIterations = 600_000;

    public string CertificatePath { get; init; } = "";
    public string CertificatePassword { get; init; } = "";
    public string RecoveryPassphrase { get; init; } = "";
    public bool UsesCertificate => CertificatePath.Length > 0;
    public bool UsesPassphrase => RecoveryPassphrase.Length > 0;

    public static KeyProtectionOptions Load(IConfiguration configuration)
    {
        var path = (configuration["School:DataProtection:CertificatePath"] ?? "").Trim();
        var password = configuration["School:DataProtection:CertificatePassword"] ?? "";
        var passphrase = configuration["School:DataProtection:RecoveryPassphrase"] ?? "";
        if (path.Length > 0 && passphrase.Length > 0)
            throw new InvalidOperationException("School:DataProtection 请只配置证书或恢复口令其中一种");
        if (path.Length > 0 && !Path.IsPathRooted(path))
            throw new InvalidOperationException("School:DataProtection:CertificatePath 必须是绝对路径");
        if (passphrase.Length > 0 && Validation.Password(passphrase) != null)
            throw new InvalidOperationException("School:DataProtection:RecoveryPassphrase " + Validation.Password(passphrase));
        return new KeyProtectionOptions { CertificatePath = path, CertificatePassword = password, RecoveryPassphrase = passphrase };
    }
}

public sealed class KeyProtectionMaterial : IDisposable
{
    public const string RecoveryFileName = "recovery-certificate.protected";

    public string Mode { get; private init; } = "dpapi";
    public bool Portable => Mode is "certificate" or "passphrase";
    public string Summary { get; private init; } = "";
    public int RewrappedKeys { get; private init; }
    public X509Certificate2? Certificate { get; private init; }

    public static KeyProtectionMaterial Prepare(string keysDirectory, KeyProtectionOptions options, ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        Directory.CreateDirectory(keysDirectory);
        if (options.UsesCertificate)
        {
            var certificate = LoadSchoolCertificate(options);
            var rewrapped = RewrapMachineKeys(keysDirectory, certificate, logger);
            logger.LogInformation("DataProtection keys are protected with the configured certificate.");
            return new KeyProtectionMaterial
            {
                Mode = "certificate",
                Summary = "密钥使用学校证书保护。请把 PFX 和密码保存在服务器以外；新系统配置同一证书后再恢复。",
                RewrappedKeys = rewrapped,
                Certificate = certificate
            };
        }
        if (options.UsesPassphrase)
        {
            var certificate = LoadOrCreateRecoveryCertificate(keysDirectory, options.RecoveryPassphrase);
            var rewrapped = RewrapMachineKeys(keysDirectory, certificate, logger);
            logger.LogInformation("DataProtection keys are protected with the recovery passphrase.");
            return new KeyProtectionMaterial
            {
                Mode = "passphrase",
                Summary = "密钥使用恢复口令保护。请把这句口令抄写到服务器以外。备份包里没有口令。",
                RewrappedKeys = rewrapped,
                Certificate = certificate
            };
        }
        return new KeyProtectionMaterial
        {
            Mode = OperatingSystem.IsWindows() ? "dpapi" : "none",
            Summary = OperatingSystem.IsWindows()
                ? "当前密钥只用本机 Windows 保护。重装系统或更换服务器后，已备份的设备凭据无法解密。可在 server.json 配置证书或恢复口令。"
                : "当前系统没有配置可携带的密钥保护。"
        };
    }

    /// <summary>
    /// Opens the certificate that can read <paramref name="keysDirectory"/> without creating
    /// a new one. Validation and backup inspection use this so a check never writes keys.
    /// </summary>
    public static X509Certificate2? OpenForRead(string keysDirectory, KeyProtectionOptions options)
    {
        if (options.UsesCertificate) return LoadSchoolCertificate(options);
        if (!options.UsesPassphrase) return null;
        var path = Path.Combine(keysDirectory, RecoveryFileName);
        if (!File.Exists(path)) return null;
        return OpenRecoveryCertificate(path, options.RecoveryPassphrase);
    }

    public static IDataProtectionBuilder Apply(IDataProtectionBuilder builder, X509Certificate2? certificate)
    {
        if (certificate != null) return builder.ProtectKeysWithCertificate(certificate);
        if (OperatingSystem.IsWindows()) return builder.ProtectKeysWithDpapi(protectToLocalMachine: true);
        return builder;
    }

    public static ServiceProvider CreateReader(string keysDirectory, X509Certificate2? certificate)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        Apply(services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keysDirectory))
            .SetApplicationName("SchoolTraining")
            .DisableAutomaticKeyGeneration(), certificate);
        return services.BuildServiceProvider();
    }

    public static string Describe(string keysDirectory)
    {
        var sawDpapi = false;
        var sawCertificate = false;
        if (Directory.Exists(keysDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(keysDirectory, "*.xml"))
            {
                XElement root;
                try { root = XElement.Load(file); }
                catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { continue; }
                foreach (var secret in root.DescendantsAndSelf().Where(element => element.Name.LocalName == "encryptedSecret"))
                {
                    var type = (string?)secret.Attribute("decryptorType") ?? "";
                    if (type.Contains("DpapiXmlDecryptor", StringComparison.Ordinal)) sawDpapi = true;
                    else if (type.Contains("EncryptedXmlDecryptor", StringComparison.Ordinal)) sawCertificate = true;
                }
            }
        }
        if (sawDpapi) return "dpapi";
        if (File.Exists(Path.Combine(keysDirectory, RecoveryFileName))) return "passphrase";
        if (sawCertificate) return "certificate";
        return OperatingSystem.IsWindows() ? "dpapi" : "none";
    }

    public void Dispose() => Certificate?.Dispose();

    static X509Certificate2 LoadSchoolCertificate(KeyProtectionOptions options)
    {
        if (!File.Exists(options.CertificatePath))
            throw new InvalidOperationException("数据保护证书不存在：" + options.CertificatePath);
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(options.CertificatePath, options.CertificatePassword, KeyStorageFlags);
            if (!certificate.HasPrivateKey)
            {
                certificate.Dispose();
                throw new InvalidOperationException("数据保护证书没有私钥，无法解密备份密钥");
            }
            return certificate;
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw new InvalidOperationException("无法读取数据保护证书，请确认 PFX 路径和密码", ex);
        }
    }

    static X509Certificate2 LoadOrCreateRecoveryCertificate(string keysDirectory, string passphrase)
    {
        var path = Path.Combine(keysDirectory, RecoveryFileName);
        if (File.Exists(path)) return OpenRecoveryCertificate(path, passphrase);
        var rsa = RSA.Create(2048);
        try
        {
            var request = new CertificateRequest("CN=SchoolTraining Recovery", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
            var pfxPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var pfx = generated.Export(X509ContentType.Pfx, pfxPassword);
            WriteRecoveryFile(path, passphrase, pfxPassword, pfx);
            return X509CertificateLoader.LoadPkcs12(pfx, pfxPassword, KeyStorageFlags);
        }
        finally { rsa.Dispose(); }
    }

    static X509Certificate2 OpenRecoveryCertificate(string path, string passphrase)
    {
        byte[] plain;
        try { plain = ReadRecoveryFile(path, passphrase); }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("恢复口令不正确，无法打开密钥证书", ex);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidOperationException("恢复密钥文件无法读取", ex);
        }
        var passwordLength = BitConverter.ToInt32(plain, 0);
        if (passwordLength < 1 || passwordLength > 256 || plain.Length < 4 + passwordLength)
            throw new InvalidOperationException("恢复密钥文件无法读取");
        var password = Encoding.UTF8.GetString(plain, 4, passwordLength);
        var pfx = plain.AsSpan(4 + passwordLength).ToArray();
        CryptographicOperations.ZeroMemory(plain);
        try { return X509CertificateLoader.LoadPkcs12(pfx, password, KeyStorageFlags); }
        catch (CryptographicException ex) { throw new InvalidOperationException("恢复密钥文件无法读取", ex); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    static void WriteRecoveryFile(string path, string passphrase, string pfxPassword, byte[] pfx)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(pfxPassword);
        var plain = new byte[4 + passwordBytes.Length + pfx.Length];
        BitConverter.TryWriteBytes(plain.AsSpan(0, 4), passwordBytes.Length);
        passwordBytes.CopyTo(plain.AsSpan(4));
        pfx.CopyTo(plain.AsSpan(4 + passwordBytes.Length));
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, KeyProtectionOptions.RecoveryIterations, HashAlgorithmName.SHA256, 32);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        try
        {
            using (var aes = new AesGcm(key, tag.Length)) aes.Encrypt(nonce, plain, cipher, tag);
            var pending = path + ".pending";
            using (var stream = File.Create(pending))
            {
                stream.Write(Encoding.ASCII.GetBytes("STKR"));
                using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
                {
                    writer.Write(1);
                    writer.Write(KeyProtectionOptions.RecoveryIterations);
                }
                stream.Write(salt);
                stream.Write(nonce);
                stream.Write(tag);
                stream.Write(cipher);
            }
            File.Move(pending, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(passwordBytes);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    static byte[] ReadRecoveryFile(string path, string passphrase)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 4 + 4 + 4 + 16 + 12 + 16 || Encoding.ASCII.GetString(bytes, 0, 4) != "STKR")
            throw new InvalidDataException("恢复密钥文件无法读取");
        var version = BitConverter.ToInt32(bytes, 4);
        var iterations = BitConverter.ToInt32(bytes, 8);
        if (version != 1 || iterations is < 100_000 or > 2_000_000) throw new InvalidDataException("恢复密钥文件无法读取");
        var salt = bytes.AsSpan(12, 16).ToArray();
        var nonce = bytes.AsSpan(28, 12).ToArray();
        var tag = bytes.AsSpan(40, 16).ToArray();
        var cipher = bytes.AsSpan(56).ToArray();
        if (cipher.Length is < 1 or > 256 * 1024) throw new InvalidDataException("恢复密钥文件无法读取");
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    static int RewrapMachineKeys(string keysDirectory, X509Certificate2 certificate, ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var encryptor = new CertificateXmlEncryptor(certificate, NullLoggerFactory.Instance);
        var decryptor = new DpapiXmlDecryptor();
        var rewritten = 0;
        foreach (var file in Directory.EnumerateFiles(keysDirectory, "*.xml"))
        {
            var document = XElement.Load(file);
            var secrets = document.DescendantsAndSelf().Where(element => element.Name.LocalName == "encryptedSecret").ToArray();
            var changed = false;
            foreach (var secret in secrets)
            {
                var type = (string?)secret.Attribute("decryptorType") ?? "";
                if (!type.Contains("DpapiXmlDecryptor", StringComparison.Ordinal)) continue;
                var plain = decryptor.Decrypt(secret.Elements().Single());
                var encrypted = encryptor.Encrypt(plain);
                secret.ReplaceWith(new XElement(secret.Name,
                    new XAttribute("decryptorType", encrypted.DecryptorType.AssemblyQualifiedName!),
                    encrypted.EncryptedElement));
                changed = true;
            }
            if (!changed) continue;
            var pending = file + ".pending";
            document.Save(pending);
            File.Move(pending, file, overwrite: true);
            rewritten++;
            logger.LogInformation("Rewrapped a DataProtection key so it can move with the backup: {File}", Path.GetFileName(file));
        }
        return rewritten;
    }

    static X509KeyStorageFlags KeyStorageFlags => X509KeyStorageFlags.EphemeralKeySet | X509KeyStorageFlags.Exportable;
}
