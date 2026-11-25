namespace School.Training;

/// <summary>
/// Host settings for a Cloud Agent Apps demo. Windows service installation stays on the
/// normal path; this only changes the process when demo mode is explicitly switched on.
/// </summary>
public static class DemoHost
{
    /// <summary>
    /// Published demonstration password. It is not a credential for a real school.
    /// The login page shows it so a customer can open the hosted link and sign in.
    /// </summary>
    public const string Password = "Demo12345678";

    public const string SchoolName = "演示职业技术学院";
    public const string AdminUserName = "demo";

    public static bool IsEnabled(IConfiguration configuration)
    {
        var value = configuration["School:Demo"];
        if (string.IsNullOrWhiteSpace(value)) value = Environment.GetEnvironmentVariable("SCHOOL_DEMO");
        return value is "1" or "true" or "True" or "TRUE";
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
