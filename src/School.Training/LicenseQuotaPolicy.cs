using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace School.Training;

public enum LicensedResource { Cabinets, Devices, Nodes, VirtualMachines, Assignments }

public sealed record LicenseQuota(string Code, string Name, long Used, int? Limit);

public sealed class LicenseQuotaPolicy(IConfiguration configuration, IWebHostEnvironment environment)
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    private const string ActivationPrefix = "licenseQuota:";

    private static readonly Dictionary<LicensedResource, (string Code, string Name)> Definitions = new()
    {
        [LicensedResource.Cabinets] = ("TRAINING.CABINETS", "机柜"),
        [LicensedResource.Devices] = ("TRAINING.DEVICES", "实训设备"),
        [LicensedResource.Nodes] = ("TRAINING.NODES", "集群节点"),
        [LicensedResource.VirtualMachines] = ("TRAINING.VMS", "虚拟机"),
        [LicensedResource.Assignments] = ("TRAINING.ASSIGNMENTS", "节点账号分配")
    };

    public string PublicKeyPath => Environment.GetEnvironmentVariable("SCHOOL_LICENSE_PUBLIC_KEY")
        ?? configuration["School:LicensePublicKey"]
        ?? Path.Combine(environment.ContentRootPath, "license-public.pem");

    public async Task<IReadOnlyList<LicenseQuota>> ReadQuotasAsync(SchoolDbContext db, CancellationToken ct)
    {
        var limits = await ReadLimitsAsync(db, ct);
        var result = new List<LicenseQuota>(Definitions.Count);
        foreach (var (resource, definition) in Definitions)
        {
            result.Add(new(definition.Code, definition.Name, await CountAsync(db, resource, ct), limits.GetValueOrDefault(resource)));
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<LicensedResource, int?>> ReadLimitsAsync(SchoolDbContext db, CancellationToken ct)
    {
        var limits = new Dictionary<LicensedResource, int?>();
        var enabled = await db.Settings.AsNoTracking().Where(row => row.Id.StartsWith(ActivationPrefix)).Select(row => row.Id).ToListAsync(ct);
        foreach (var (resource, definition) in Definitions)
            if (enabled.Contains(ActivationPrefix + definition.Code, StringComparer.Ordinal)) limits[resource] = 0;
        var records = await db.Licenses.AsNoTracking().Select(x => x.PayloadJson).ToListAsync(ct);
        if (records.Count == 0) return limits;

        var now = DateTime.UtcNow;
        using var key = await OpenVerificationKeyAsync(ct);
        foreach (var json in records)
        {
            if (!TryReadStored(json, out var payload, out var envelope) || envelope is null) continue;
            var effective = key is not null && Verify(envelope, key) && IsEffective(payload, now);
            foreach (var (resource, definition) in Definitions)
            {
                var matching = payload.Resources.Where(entry => entry.AuthorizationCode == definition.Code).ToArray();
                if (matching.Length == 0) continue;
                var candidate = effective && matching.Length == 1 ? Math.Max(0, matching[0].Quantity) : 0;
                limits[resource] = Math.Max(limits.GetValueOrDefault(resource) ?? 0, candidate);
            }
        }
        return limits;
    }

    public async Task<IResult?> CheckAllocationAsync(SchoolDbContext db, LicensedResource resource, int additional, CancellationToken ct)
        => await CheckAllocationAsync(db, await ReadLimitsAsync(db, ct), resource, additional, ct);

    public static async Task<IResult?> CheckAllocationAsync(SchoolDbContext db, IReadOnlyDictionary<LicensedResource, int?> limits, LicensedResource resource, int additional, CancellationToken ct)
    {
        if (limits.GetValueOrDefault(resource) is not { } limit) return null;
        var used = await CountAsync(db, resource, ct);
        return HasCapacity(limit, used, additional) ? null : QuotaExceeded(resource, used, limit);
    }

    public static bool HasCapacity(int? limit, long used, int requested) => limit is null || used + requested <= limit;

    public static IResult QuotaExceeded(LicensedResource resource, long used, int limit)
    {
        var (code, name) = Definitions[resource];
        return Results.Conflict(new { message = $"{name}已使用 {used} 项，许可证额度为 {limit} 项。请释放配额或导入有效授权后重试。", code, used, limit });
    }

    public static bool UniqueManagedCodes(LicensePayload payload) => payload.Resources
        .Where(entry => Definitions.Values.Any(definition => definition.Code == entry.AuthorizationCode))
        .GroupBy(entry => entry.AuthorizationCode, StringComparer.Ordinal)
        .All(group => group.Count() == 1);

    public static async Task MarkActivatedAsync(SchoolDbContext db, IEnumerable<string> codes, CancellationToken ct)
    {
        var knownCodes = Definitions.Values.Select(definition => definition.Code).ToHashSet(StringComparer.Ordinal);
        var keys = codes.Where(knownCodes.Contains).Select(code => ActivationPrefix + code).Distinct(StringComparer.Ordinal).ToArray();
        if (keys.Length == 0) return;
        var existing = await db.Settings.Where(row => keys.Contains(row.Id)).Select(row => row.Id).ToListAsync(ct);
        db.Settings.AddRange(keys.Except(existing, StringComparer.Ordinal).Select(id => new AppSetting { Id = id, Value = "true" }));
    }

    public static bool TryReadStored(string json, out LicensePayload payload, out LicenseEnvelope? envelope)
    {
        payload = null!;
        envelope = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (document.RootElement.TryGetProperty("payload", out _))
            {
                envelope = document.RootElement.Deserialize<LicenseEnvelope>(JsonOptions);
                if (envelope is null || !TryReadEnvelope(envelope, out payload)) return false;
            }
            else
            {
                var legacy = document.RootElement.Deserialize<LicensePayload>(JsonOptions);
                if (legacy is null) return false;
                payload = legacy;
            }
            return payload.Resources.All(entry => entry is not null);
        }
        catch (JsonException) { return false; }
    }

    public static bool TryReadEnvelope(LicenseEnvelope envelope, out LicensePayload payload)
    {
        payload = null!;
        try
        {
            var parsed = JsonSerializer.Deserialize<LicensePayload>(Convert.FromBase64String(envelope.Payload), JsonOptions);
            if (parsed is null || parsed.Resources.Any(entry => entry is null)) return false;
            payload = parsed;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException) { return false; }
    }

    public static bool Verify(LicenseEnvelope envelope, RSA key)
    {
        try
        {
            return key.VerifyData(Convert.FromBase64String(envelope.Payload), Convert.FromBase64String(envelope.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException) { return false; }
    }

    public async Task<RSA?> OpenVerificationKeyAsync(CancellationToken ct)
    {
        if (!File.Exists(PublicKeyPath)) return null;
        var key = RSA.Create();
        try
        {
            key.ImportFromPem(await File.ReadAllTextAsync(PublicKeyPath, ct));
            return key;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or CryptographicException)
        {
            key.Dispose();
            return null;
        }
    }

    public static bool IsEffective(LicensePayload payload, DateTime now) => payload.Product == "SchoolTraining"
        && string.Equals(payload.MachineCode, OperationsEndpoints.MachineCode(), StringComparison.Ordinal)
        && payload.IssuedAt.ToUniversalTime() <= now
        && (payload.ExpiresAt is null || payload.ExpiresAt.Value.ToUniversalTime() > now);

    private static Task<long> CountAsync(SchoolDbContext db, LicensedResource resource, CancellationToken ct) => resource switch
    {
        LicensedResource.Cabinets => db.Cabinets.LongCountAsync(ct),
        LicensedResource.Devices => db.Devices.LongCountAsync(ct),
        LicensedResource.Nodes => db.Nodes.LongCountAsync(ct),
        LicensedResource.VirtualMachines => db.VirtualMachines.LongCountAsync(ct),
        LicensedResource.Assignments => db.NodeAccountAssignments.LongCountAsync(ct),
        _ => throw new ArgumentOutOfRangeException(nameof(resource))
    };
}
