using System.Security.Cryptography;
using System.Text;

namespace School.Training;

/// <summary>
/// Host settings for a Cloud Agent Apps demo. Windows service installation stays on the
/// normal path; this only changes the process when demo mode is explicitly switched on.
/// </summary>
public static class DemoHost
{
    public const string SchoolName = "演示职业技术学院";
    public const string AdminUserName = "demo";
    public const string PasswordVariable = "SCHOOL_DEMO_PASSWORD";

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
    /// The shared demonstration password comes from the environment. A missing or short
    /// value is replaced once and stored outside the web root; only its hash is kept in the database.
    /// </summary>
    public static string ResolvePassword(string? configured, string passwordFilePath)
    {
        EnsurePasswordLocation(passwordFilePath);
        var supplied = configured?.Trim() ?? "";
        if (supplied.Length >= Validation.PasswordMinLength)
        {
            WritePasswordFile(passwordFilePath, supplied);
            return supplied;
        }
        if (TryReadPassword(passwordFilePath, out var existing)) return existing;
        var generated = GeneratePassword();
        WritePasswordFile(passwordFilePath, generated);
        return generated;
    }

    public static string GeneratePassword()
    {
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        var alphabet = letters + digits;
        var chars = new char[24];
        chars[0] = letters[RandomNumberGenerator.GetInt32(letters.Length)];
        chars[1] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        for (var i = 2; i < chars.Length; i++) chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var swap = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[swap]) = (chars[swap], chars[i]);
        }
        return new string(chars);
    }

    public static string PasswordFilePath(string dataDirectory)
    {
        var full = Path.GetFullPath(dataDirectory);
        var parent = Directory.GetParent(full)?.FullName;
        var path = parent != null && string.Equals(Path.GetFileName(parent), ".local", StringComparison.Ordinal)
            ? Path.Combine(parent, "demo-password.txt")
            : Path.Combine(full, "demo-password.txt");
        EnsurePasswordLocation(path);
        return path;
    }

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

    static bool TryReadPassword(string path, out string password)
    {
        password = "";
        if (!File.Exists(path)) return false;
        var text = File.ReadAllText(path).Trim();
        if (Validation.Password(text) != null) return false;
        password = text;
        return true;
    }

    static void WritePasswordFile(string path, string password)
    {
        EnsurePasswordLocation(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(path))
        {
            try
            {
                if (string.Equals(File.ReadAllText(path).Trim(), password, StringComparison.Ordinal)) return;
            }
            catch (IOException) { }
        }
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, password + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temporary, path, overwrite: true);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    static void EnsurePasswordLocation(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        if (normalized.Contains("/wwwroot/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/wwwroot", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/docs/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("/publish/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("演示口令文件不能放在网站目录、文档或发布目录。");
    }
}
