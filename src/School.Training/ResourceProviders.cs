namespace School.Training;

/// <summary>
/// Identifiers and response helpers shared by the infrastructure endpoints.
/// </summary>
public static class ResourceProviders
{
    public const string Simulated = "simulated";

    public static bool IsSimulated(string? provider) => string.Equals(provider, Simulated, StringComparison.Ordinal);

    public static IResult Unsupported(string message) => Results.Json(new { message }, statusCode: 501);
}
