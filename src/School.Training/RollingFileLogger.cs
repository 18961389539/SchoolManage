namespace School.Training;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    readonly string logDirectory;
    readonly StorageOptions options;
    readonly object gate = new();
    string? currentFile;
    DateTime currentDate;
    long writeFailures;

    public RollingFileLoggerProvider(string directory, StorageOptions options)
    {
        logDirectory = directory;
        this.options = options;
        Directory.CreateDirectory(logDirectory);
        lock (gate) Cleanup(DateTime.UtcNow);
    }

    public long WriteFailures => Interlocked.Read(ref writeFailures);
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    void Cleanup(DateTime now)
    {
        FileRetention.Apply(logDirectory, "service-*.log", int.MaxValue, options.LogMaxTotalBytes,
            now.AddDays(-options.LogRetentionDays), currentFile);
    }

    string SelectFile(DateTime now, int incomingBytes)
    {
        if (currentFile != null && currentDate == now.Date && File.Exists(currentFile)
            && new FileInfo(currentFile).Length + incomingBytes <= options.LogMaxFileBytes) return currentFile;

        if (currentDate != now.Date) Cleanup(now);
        currentDate = now.Date;
        var baseName = Path.Combine(logDirectory, $"service-{now:yyyyMMdd}.log");
        if (!File.Exists(baseName) || new FileInfo(baseName).Length + incomingBytes <= options.LogMaxFileBytes)
        {
            currentFile = baseName;
            return currentFile;
        }
        for (var index = 1; index < 1000; index++)
        {
            var candidate = Path.Combine(logDirectory, $"service-{now:yyyyMMdd}-{index:000}.log");
            if (!File.Exists(candidate) || new FileInfo(candidate).Length + incomingBytes <= options.LogMaxFileBytes)
            {
                currentFile = candidate;
                return currentFile;
            }
        }
        currentFile = Path.Combine(logDirectory, $"service-{now:yyyyMMdd}-{now:HHmmssfff}.log");
        return currentFile;
    }

    sealed class FileLogger(RollingFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        // The level comes from School:Logs:MinimumLevel so an operator can reduce synchronous
        // file writes without rebuilding the service.
        public bool IsEnabled(LogLevel level) => level >= owner.options.LogMinimumLevel && level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var now = DateTime.UtcNow;
            var message = $"{now:O} [{level}] {category}: {formatter(state, exception)}{Environment.NewLine}";
            if (exception != null) message += exception + Environment.NewLine;
            var bytes = System.Text.Encoding.UTF8.GetByteCount(message);
            lock (owner.gate)
            {
                var target = owner.SelectFile(now, bytes);
                try { File.AppendAllText(target, message); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Interlocked.Increment(ref owner.writeFailures);
                    try { Console.Error.WriteLine($"{now:O} [LoggerFailure] {ex.Message}"); } catch { }
                }
            }
        }
    }
}
