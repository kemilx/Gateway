using Gateway.LoadHarness;
using System.Text.Json;
using Xunit;
using Barrier = Gateway.LoadHarness.Barrier;

namespace Gateway.Tests;

[Collection("Isolated harness")]
public sealed class MemoryBodyValidationTests
{
    private static readonly byte[] Expected = CompareHarness.Expected(true);

    [Fact]
    public async Task Invalid_json_with_same_length_and_mask_is_rejected()
    {
        var body = (byte[])Expected.Clone();
        body[0] = (byte)'!';
        var barrier = new Barrier(1);
        barrier.Release.TrySetResult();
        using var stream = new ControlledWriteStream(barrier, Expected);
        await stream.WriteAsync(body);
        Assert.False(stream.Canary);
    }

    [Fact]
    public async Task Modified_nonsensitive_field_with_same_length_and_mask_is_rejected()
    {
        var body = (byte[])Expected.Clone();
        var index = body.AsSpan().IndexOf("\"a\":1"u8);
        Assert.True(index >= 0);
        body[index + 4] = (byte)'2';
        var barrier = new Barrier(1);
        barrier.Release.TrySetResult();
        using var stream = new ControlledWriteStream(barrier, Expected);
        await stream.WriteAsync(body);
        Assert.False(stream.Canary);
    }

    [Fact]
    public async Task Exact_masked_payload_is_accepted()
    {
        using var stream = OpenStream();
        await stream.WriteAsync(Expected);
        Assert.True(stream.Canary);
    }

    [Fact]
    public async Task Incorrect_sizes_and_unmasked_payload_are_rejected()
    {
        foreach (var body in new[] { Expected[..^1], [.. Expected, (byte)0], Common.Payload() })
        {
            using var stream = OpenStream();
            await stream.WriteAsync(body);
            Assert.False(stream.Canary);
        }
    }

    [Fact]
    public async Task Missing_or_multiple_writes_cannot_validate()
    {
        using var stream = OpenStream();
        Assert.False(stream.Canary);
        await stream.WriteAsync(Expected);
        Assert.True(stream.Canary);
        await stream.WriteAsync(Expected);
        Assert.False(stream.Canary);

        using var invalidFirst = OpenStream();
        await invalidFirst.WriteAsync(Expected[..^1]);
        Assert.False(invalidFirst.Canary);
        await invalidFirst.WriteAsync(Expected);
        Assert.False(invalidFirst.Canary);
    }

    [Fact]
    public async Task Run_reports_real_workers_and_released_budgets()
    {
        var output = Path.Combine(Path.GetTempPath(), "opencode", "memory-body-fix", Guid.NewGuid().ToString("N"));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        await MemoryHarness.Run(output, deadline.Token);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "memory.json")));
        var report = document.RootElement;
        Assert.Equal("passed", report.GetProperty("status").GetString());
        var workers = report.GetProperty("workers").EnumerateArray().ToArray();
        Assert.Equal(new[] { 1, 2, 4, 8 }, workers.Select(w => w.GetProperty("concurrency").GetInt32()));
        foreach (var worker in workers)
        {
            var n = worker.GetProperty("concurrency").GetInt32();
            Assert.Equal("passed", worker.GetProperty("status").GetString());
            Assert.True(worker.GetProperty("release").GetBoolean());
            Assert.True(worker.GetProperty("canary").GetBoolean());
            Assert.Equal(n + (n == 8 ? 1 : 0), worker.GetProperty("outcomeCount").GetInt64());
            Assert.Equal(n, worker.GetProperty("durationCount").GetInt64());
            var samples = worker.GetProperty("samples").EnumerateArray().ToArray();
            var baseline = Assert.Single(samples, s => s.GetProperty("phase").GetString() == "baseline");
            Assert.Equal(0, baseline.GetProperty("active").GetInt32());
            Assert.Equal(0, baseline.GetProperty("reserved").GetInt64());
            var released = Assert.Single(samples, s => s.GetProperty("phase").GetString() == "post-release");
            Assert.Equal(0, released.GetProperty("active").GetInt32());
            Assert.Equal(0, released.GetProperty("reserved").GetInt64());
            var saturation = worker.GetProperty("saturation");
            Assert.Equal(n == 8, saturation.GetProperty("tested").GetBoolean());
            if (n == 8)
            {
                Assert.Equal(503, saturation.GetProperty("statusCode").GetInt32());
                Assert.Equal(0, saturation.GetProperty("originReads").GetInt32());
                Assert.True(saturation.GetProperty("budgetUnchanged").GetBoolean());
            }
        }
        Assert.Contains("expected masked payload", report.GetProperty("notes").GetString());
    }

    private static ControlledWriteStream OpenStream()
    {
        var barrier = new Barrier(1);
        barrier.Release.TrySetResult();
        return new ControlledWriteStream(barrier, Expected);
    }
}
