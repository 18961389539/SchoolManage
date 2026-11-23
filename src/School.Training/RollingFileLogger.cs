using System.Threading.Channels;

namespace School.Training;

public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    sealed record Entry(DateTime Now, string Message, int Bytes);
    readonly string logDirectory;
    readonly StorageOptions options;
    readonly object gate = new();
    readonly Channel<Entry> channel = Channel.CreateBounded<Entry>(new BoundedChannelOptions(10_000)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    readonly Task writerTask;
    string? currentFile;
    DateTime currentDate;
    long writeFailures;

    public RollingFileLoggerProvider(string directory, StorageOptions options)
    {
        logDirectory = directory;
        this.options = options;
        Directory.CreateDirectory(logDirectory);
        lock (gate) Cleanup(DateTime.UtcNow);
        writerTask = Task.Run(WriteLoop);
    }

    public long WriteFailures => Interlocked.Read(ref writeFailures);
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        channel.Writer.TryComplete();
        try { writerTask.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
    }

    async Task WriteLoop()
    {
        var batch = new List<Entry>(64);
        try
        {
            while (await channel.Reader.WaitToReadAsync())
            {
                while (batch.Count < 64 && channel.Reader.TryRead(out var entry)) batch.Add(entry);
                if (batch.Count == 0) continue;
                lock (gate)
                {
                    foreach (var entry in batch) Write(entry);
                }
                batch.Clear();
            }
        }
        catch (Exception exception)
        {
            try { Console.Error.WriteLine($"{DateTime.UtcNow:O} [LoggerFailure] writer stopped: {exception.Message}"); } catch { }
        }
    }

    void Write(Entry entry)
    {
        var target = SelectFile(entry.Now, entry.Bytes);
        try { File.AppendAllText(target, entry.Message); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref writeFailures);
            try { Console.Error.WriteLine($"{entry.Now:O} [LoggerFailure] {exception.Message}"); } catch { }
        }
    }

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
        // The level comes from School:Logs:MinimumLevel so an operator can reduce log volume
        // without rebuilding the service.
        public bool IsEnabled(LogLevel level) => level >= owner.options.LogMinimumLevel && level != LogLevel.None;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var now = DateTime.UtcNow;
            var message = $"{now:O} [{level}] {category}: {formatter(state, exception)}{Environment.NewLine}";
            if (exception != null) message += exception + Environment.NewLine;
            var bytes = System.Text.Encoding.UTF8.GetByteCount(message);
            if (!owner.channel.Writer.TryWrite(new Entry(now, message, bytes)))
                Interlocked.Increment(ref owner.writeFailures);
        }
    }
}
