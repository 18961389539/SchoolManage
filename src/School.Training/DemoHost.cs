namespace School.Training;

/// <summary>
/// Host settings for a Cloud Agent Apps demo. Windows service installation stays on the
/// normal path; this only changes the process when demo mode is explicitly switched on.
/// </summary>
public static class DemoHost
{
    /// <summary>
    /// Environment variable that supplies the demonstration password. The value is never
    /// written into the repository or returned to the browser.
    /// </summary>
    public const string PasswordEnvironmentVariable = "SCHOOL_DEMO_PASSWORD";

    public const string SchoolName = "演示职业技术学院";
    public const string AdminUserName = "demo";

    public static bool IsEnabled(IConfiguration configuration)
    {
        var value = configuration["School:Demo"];
        if (string.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable("SCHOOL_DEMO");
        return value is "1" or "true" or "True" or "TRUE";
    }

    /// <summary>
    /// Reads the demonstration password from configuration or the environment.
    /// A missing or weak value means demonstration accounts are not created.
    /// </summary>
    public static string? ConfiguredPassword(IConfiguration configuration)
    {
        var value = configuration["School:DemoPassword"];
        if (string.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
        value = value?.Trim();
        return string.IsNullOrEmpty(value) || Validation.Password(value) != null ? null : value;
    }

    public static string ListenUrl(string? port)
    {
        if (!int.TryParse(port, out var parsed) || parsed is < 1 or > 65535) parsed = 5080;
        return $"http://0.0.0.0:{parsed}";
    }

    public static string HostFilter(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? "*" : configured.Trim();

    /// <summary>
    /// Binds every IPv4 interface on PORT and accepts the hostname chosen at publish time.
    /// A launch profile's localhost URL must not win, or the hosted proxy cannot reach the process.
    /// </summary>
    public static void Apply(WebApplicationBuilder builder)
    {
        builder.Configuration["AllowedHosts"] = HostFilter(Environment.GetEnvironmentVariable("SCHOOL_DEMO_HOSTS"));
        var urls = Environment.GetEnvironmentVariable("SCHOOL_DEMO_URLS");
        builder.Configuration["urls"] = string.IsNullOrWhiteSpace(urls) ? ListenUrl(Environment.GetEnvironmentVariable("PORT")) : urls.Trim();
        builder.WebHost.UseUrls(builder.Configuration["urls"]!);
    }
}
