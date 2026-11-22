namespace School.Training;

/// <summary>
/// Transport and payload limits shared by Kestrel, form parsing, the import endpoint and
/// the status payload the web client reads. One constant keeps the browser and the server
/// from disagreeing about which file sizes are acceptable.
/// </summary>
public static class Limits
{
    public const int MaxUploadBytes = 16 * 1024 * 1024;
    public const int MaxUploadMegabytes = MaxUploadBytes / (1024 * 1024);
}
