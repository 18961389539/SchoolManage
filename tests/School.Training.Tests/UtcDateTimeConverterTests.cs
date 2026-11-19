using System.Text.Json;
using School.Training;

namespace School.Training.Tests;

public sealed class UtcDateTimeConverterTests
{
    static readonly JsonSerializerOptions Options = new() { Converters = { new UtcDateTimeConverter() } };

    static DateTime Read(string json) => JsonSerializer.Deserialize<DateTime>(json, Options);
    static string Write(DateTime value) => JsonSerializer.Serialize(value, Options);

    [Fact]
    public void Read_TreatsUnspecifiedTimestampAsUtc()
    {
        var value = Read("\"2026-11-19T08:30:00\"");

        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(new DateTime(2026, 11, 19, 8, 30, 0, DateTimeKind.Utc), value);
    }

    [Fact]
    public void Read_KeepsUtcTimestamp()
    {
        var value = Read("\"2026-11-19T08:30:00Z\"");

        Assert.Equal(DateTimeKind.Utc, value.Kind);
        Assert.Equal(new DateTime(2026, 11, 19, 8, 30, 0, DateTimeKind.Utc), value);
    }

    [Fact]
    public void Write_EmitsRoundTripUtcWithDesignator()
    {
        var text = Write(new DateTime(2026, 11, 19, 8, 30, 0, DateTimeKind.Utc));

        Assert.Equal("\"2026-11-19T08:30:00.0000000Z\"", text);
    }

    [Fact]
    public void Write_TreatsUnspecifiedValueAsUtc()
    {
        var text = Write(DateTime.SpecifyKind(new DateTime(2026, 11, 19, 8, 30, 0), DateTimeKind.Unspecified));

        Assert.Equal("\"2026-11-19T08:30:00.0000000Z\"", text);
    }

    [Fact]
    public void RoundTrip_PreservesInstant()
    {
        var original = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var restored = Read(Write(original));

        Assert.Equal(original, restored);
        Assert.Equal(DateTimeKind.Utc, restored.Kind);
    }
}
