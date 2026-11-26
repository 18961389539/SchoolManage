using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using School.Training;

namespace School.Training.Tests;

public sealed class KeyProtectionTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "school-keys-" + Guid.NewGuid().ToString("N"));

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Recovery_passphrase_lets_another_machine_read_credentials_without_dpapi()
    {
        var paths = new AppPaths(root);
        var cipher = ProtectWithDpapi(paths.Keys, "cabinet-secret");
        using var material = KeyProtectionMaterial.Prepare(paths.Keys, new KeyProtectionOptions { RecoveryPassphrase = "RecoverPass2026" });

        Assert.Equal("passphrase", material.Mode);
        Assert.True(material.RewrappedKeys >= 1);
        Assert.Equal("passphrase", KeyProtectionMaterial.Describe(paths.Keys));
        Assert.DoesNotContain("DpapiXmlDecryptor", File.ReadAllText(Directory.GetFiles(paths.Keys, "key-*.xml").Single()), StringComparison.Ordinal);
        Assert.Equal("cabinet-secret", UnprotectWithCertificate(paths.Keys, material.Certificate!, cipher));
        Assert.Throws<InvalidOperationException>(() => KeyProtectionMaterial.OpenForRead(paths.Keys, new KeyProtectionOptions { RecoveryPassphrase = "WrongPass2026xx" }));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void School_certificate_protects_keys_without_writing_a_recovery_file()
    {
        var paths = new AppPaths(root);
        var pfxPath = Path.Combine(root, "school.pfx");
        const string pfxPassword = "CertPass2026";
        WriteCertificate(pfxPath, pfxPassword);
        var cipher = ProtectWithDpapi(paths.Keys, "node-secret");
        using var material = KeyProtectionMaterial.Prepare(paths.Keys, new KeyProtectionOptions
        {
            CertificatePath = pfxPath,
            CertificatePassword = pfxPassword
        });

        Assert.Equal("certificate", material.Mode);
        Assert.False(File.Exists(Path.Combine(paths.Keys, KeyProtectionMaterial.RecoveryFileName)));
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, pfxPassword, X509KeyStorageFlags.EphemeralKeySet);
        Assert.Equal("node-secret", UnprotectWithCertificate(paths.Keys, certificate, cipher));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task Backup_contains_the_recovery_certificate_and_rejects_an_unexpected_entry()
    {
        var paths = new AppPaths(root);
        ProtectWithDpapi(paths.Keys, "saved");
        using var material = KeyProtectionMaterial.Prepare(paths.Keys, new KeyProtectionOptions { RecoveryPassphrase = "RecoverPass2026" });
        await using var db = new SchoolDbContext(new DbContextOptionsBuilder<SchoolDbContext>().UseSqlite(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = paths.Database, Pooling = false }.ToString()).Options);
        await db.Database.EnsureCreatedAsync();
        var backup = await BackupService.CreateAsync(paths, db, new StorageOptions(), "admin", "创建备份", _ => long.MaxValue, CancellationToken.None);
        Assert.True(backup.Success, backup.Message);
        using (var archive = ZipFile.OpenRead(Path.Combine(paths.Backups, backup.FileName)))
            Assert.NotNull(archive.GetEntry("keys/" + KeyProtectionMaterial.RecoveryFileName));

        var unexpected = Path.Combine(root, "unexpected.zip");
        using (var archive = ZipFile.Open(unexpected, ZipArchiveMode.Create))
            archive.CreateEntry("readme.txt");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => BackupInspection.InspectZipAsync(unexpected, new KeyProtectionOptions(), CancellationToken.None));
        Assert.Contains("意外", error.Message);
        GC.KeepAlive(material);
    }

    [Fact]
    public void Load_rejects_configuring_a_certificate_and_a_passphrase_together()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["School:DataProtection:CertificatePath"] = @"D:\secrets\school.pfx",
            ["School:DataProtection:RecoveryPassphrase"] = "RecoverPass2026"
        }).Build();
        var error = Assert.Throws<InvalidOperationException>(() => KeyProtectionOptions.Load(configuration));
        Assert.Contains("其中一种", error.Message);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [SupportedOSPlatform("windows")]
    static string ProtectWithDpapi(string keys, string plaintext)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys)).SetApplicationName("SchoolTraining")
            .ProtectKeysWithDpapi(protectToLocalMachine: true);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("EquipmentCredentials.v1").Protect(plaintext);
    }

    static string UnprotectWithCertificate(string keys, X509Certificate2 certificate, string cipher)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        KeyProtectionMaterial.Apply(services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keys))
            .SetApplicationName("SchoolTraining")
            .DisableAutomaticKeyGeneration(), certificate);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("EquipmentCredentials.v1").Unprotect(cipher);
    }

    static void WriteCertificate(string path, string password)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=School Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, critical: true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
    }
}
