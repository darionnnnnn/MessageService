using System.Net;
using MessageService.Options;
using MessageService.Web.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MessageService.Web.Tests.Diagnostics;

public class RingBufferLoggerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"messageservice-ringbuffer-test-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            try { File.Delete(_dbPath); } catch { }
        }
    }

    [Fact]
    public void LogRingBuffer_When201EntriesAdded_Contains200Entries_DropsOldest_SnapshotFirstIsNewest()
    {
        var buffer = new LogRingBuffer();

        for (var i = 1; i <= 201; i++)
        {
            buffer.Add(new LogBufferEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Warning,
                "TestCategory",
                $"Message {i}",
                null));
        }

        var snapshot = buffer.Snapshot();

        Assert.Equal(200, snapshot.Count);
        Assert.Equal("Message 201", snapshot[0].Message);
        Assert.Equal("Message 2", snapshot[^1].Message);
        Assert.DoesNotContain(snapshot, e => e.Message == "Message 1");
    }

    [Fact]
    public void LogRingBuffer_Snapshot_ReturnsItemsSortedNewestToOldest()
    {
        var buffer = new LogRingBuffer();

        buffer.Add(new LogBufferEntry(DateTimeOffset.UtcNow, LogLevel.Warning, "Cat", "First", null));
        buffer.Add(new LogBufferEntry(DateTimeOffset.UtcNow, LogLevel.Error, "Cat", "Second", null));
        buffer.Add(new LogBufferEntry(DateTimeOffset.UtcNow, LogLevel.Critical, "Cat", "Third", null));

        var snapshot = buffer.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Equal("Third", snapshot[0].Message);
        Assert.Equal("Second", snapshot[1].Message);
        Assert.Equal("First", snapshot[2].Message);
    }

    [Fact]
    public void LogRingBuffer_ConcurrentAdd_ThreadSafe_NoExceptionsAndFinalCountIs200()
    {
        var buffer = new LogRingBuffer();
        const int threadCount = 8;
        const int itemsPerThread = 500;

        Parallel.For(0, threadCount, threadIndex =>
        {
            for (var i = 0; i < itemsPerThread; i++)
            {
                buffer.Add(new LogBufferEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Warning,
                    "ConcurrentCat",
                    $"Thread {threadIndex} - Item {i}",
                    null));
            }
        });

        var snapshot = buffer.Snapshot();
        Assert.Equal(200, snapshot.Count);
    }

    [Theory]
    [InlineData(LogLevel.Trace, false)]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Information, false)]
    [InlineData(LogLevel.Warning, true)]
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Critical, true)]
    [InlineData(LogLevel.None, false)]
    public void RingBufferLogger_LogLevel_FiltersMessagesBelowWarning(LogLevel level, bool expectedEnabled)
    {
        var buffer = new LogRingBuffer();
        var logger = new RingBufferLogger("TestCategory", buffer);

        var isEnabled = logger.IsEnabled(level);
        Assert.Equal(expectedEnabled, isEnabled);

        logger.Log(level, new EventId(1), "test state", null, (s, e) => $"Message for {level}");

        var snapshot = buffer.Snapshot();
        if (expectedEnabled)
        {
            Assert.Single(snapshot);
            Assert.Equal($"Message for {level}", snapshot[0].Message);
            Assert.Equal(level, snapshot[0].Level);
        }
        else
        {
            Assert.Empty(snapshot);
        }
    }

    [Fact]
    public void RingBufferLogger_Log_WithoutException_FormatsEntryWithNullSummary()
    {
        var buffer = new LogRingBuffer();
        var logger = new RingBufferLogger("OrderService", buffer);

        var before = DateTimeOffset.UtcNow;
        logger.LogWarning("Order processing delayed");
        var after = DateTimeOffset.UtcNow;

        var snapshot = buffer.Snapshot();
        var entry = Assert.Single(snapshot);

        Assert.Equal("OrderService", entry.Category);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Order processing delayed", entry.Message);
        Assert.Null(entry.ExceptionSummary);
        Assert.True(entry.TimestampUtc >= before && entry.TimestampUtc <= after);
        Assert.Equal(TimeSpan.Zero, entry.TimestampUtc.Offset);
    }

    [Fact]
    public void RingBufferLogger_Log_WithException_FormatsExceptionSummaryWithFullNameMessageAndTopStackLine()
    {
        var buffer = new LogRingBuffer();
        var logger = new RingBufferLogger("PaymentService", buffer);

        try
        {
            ThrowTestException();
        }
        catch (InvalidOperationException ex)
        {
            logger.LogError(ex, "Payment gateway failed");
        }

        var snapshot = buffer.Snapshot();
        var entry = Assert.Single(snapshot);

        Assert.Equal("PaymentService", entry.Category);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Payment gateway failed", entry.Message);
        Assert.NotNull(entry.ExceptionSummary);

        var summary = entry.ExceptionSummary;
        Assert.StartsWith("System.InvalidOperationException: Payment connection timed out", summary);
        Assert.Contains("\n", summary);
        Assert.Contains(nameof(ThrowTestException), summary);
    }

    private static void ThrowTestException()
    {
        throw new InvalidOperationException("Payment connection timed out");
    }

    [Fact]
    public void RingBufferLogger_Log_WithExceptionWithoutStackTrace_FormatsFullNameAndMessage()
    {
        var buffer = new LogRingBuffer();
        var logger = new RingBufferLogger("AuthService", buffer);

        var ex = new ArgumentException("Invalid auth token");
        logger.LogCritical(ex, "Authentication failed");

        var snapshot = buffer.Snapshot();
        var entry = Assert.Single(snapshot);

        Assert.Equal("System.ArgumentException: Invalid auth token", entry.ExceptionSummary);
    }

    [Fact]
    public void RingBufferLogger_Log_EachCallAddsSingleEntryWithoutMergingDuplicates()
    {
        var buffer = new LogRingBuffer();
        var logger = new RingBufferLogger("DuplicateTest", buffer);

        logger.LogWarning("Repeated warning");
        logger.LogWarning("Repeated warning");
        logger.LogWarning("Repeated warning");

        var snapshot = buffer.Snapshot();
        Assert.Equal(3, snapshot.Count);
        Assert.All(snapshot, e => Assert.Equal("Repeated warning", e.Message));
    }

    [Fact]
    public void RingBufferLoggerProvider_CreateLogger_SharesSameBufferAcrossLoggers()
    {
        var buffer = new LogRingBuffer();
        using var provider = new RingBufferLoggerProvider(buffer);

        var loggerA = provider.CreateLogger("CategoryA");
        var loggerB = provider.CreateLogger("CategoryB");

        loggerA.LogWarning("Warning from A");
        loggerB.LogError("Error from B");

        var snapshot = buffer.Snapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("Error from B", snapshot[0].Message);
        Assert.Equal("CategoryB", snapshot[0].Category);
        Assert.Equal("Warning from A", snapshot[1].Message);
        Assert.Equal("CategoryA", snapshot[1].Category);
    }

    [Fact]
    public void RingBufferLoggerProvider_Dispose_DoesNotThrow()
    {
        var buffer = new LogRingBuffer();
        var provider = new RingBufferLoggerProvider(buffer);

        var exception = Record.Exception(() => provider.Dispose());
        Assert.Null(exception);
    }

    [Theory]
    [InlineData("Edge")]
    [InlineData("EdgeProxy")]
    public void Program_EdgeAndEdgeProxyModes_RegisterLogRingBufferInDi(string mode)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", mode);
            builder.UseSetting("Line:ChannelSecret", "test-secret");
            builder.UseSetting("Line:OutboundHere", "false");
            if (mode == "Edge")
            {
                builder.UseSetting("Ingest:BaseUrl", "https://db-host.example");
                builder.UseSetting("Ingest:ApiKey", "test-key");
                builder.UseSetting("EdgeAdmin:AllowPlaintextSettings", "true");
            }
            else if (mode == "EdgeProxy")
            {
                builder.UseSetting("EdgeProxy:TargetBaseUrl", "http://192.0.2.10/MSLine");
            }
        });

        using var scope = factory.Services.CreateScope();
        var buffer1 = scope.ServiceProvider.GetService<LogRingBuffer>();
        var buffer2 = factory.Services.GetService<LogRingBuffer>();

        Assert.NotNull(buffer1);
        Assert.Same(buffer1, buffer2);

        var logger = scope.ServiceProvider.GetRequiredService<ILogger<RingBufferLoggerTests>>();
        logger.LogWarning("Test warning from DI logger");

        var snapshot = buffer1.Snapshot();
        Assert.Contains(snapshot, e => e.Message == "Test warning from DI logger");
    }

    [Theory]
    [InlineData("AllInOne")]
    [InlineData("Core")]
    [InlineData("Viewer")]
    public void Program_OtherModes_DoNotRegisterLogRingBufferInDi(string mode)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Deployment:Mode", mode);
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:Sqlite", $"Data Source={_dbPath}");
            builder.UseSetting("Line:OutboundHere", "false");
            builder.UseSetting("Heartbeat:Enabled", "false");
            if (mode is "AllInOne" or "Core")
            {
                builder.UseSetting("Ingest:ApiKey", "test-key");
                builder.UseSetting("Line:ChannelSecret", "test-secret");
            }
        });

        var buffer = factory.Services.GetService<LogRingBuffer>();
        Assert.Null(buffer);
    }

    [Fact]
    public void RingBufferLoggerProvider_IpAllowlistCategory_DoesNotEnterBuffer()
    {
        // EdgeProxy 在公網上，任何來源打一輪就能用白名單拒絕 Warning 灌爆 200 筆緩衝，
        // 把真正的錯誤擠掉——這個分類必須被排除（NLog 檔案照記，只是不進網頁緩衝）
        var buffer = new LogRingBuffer();
        using var provider = new RingBufferLoggerProvider(buffer);

        var excluded = provider.CreateLogger(typeof(MessageService.Web.Middleware.IpAllowlistMiddleware).FullName!);
        excluded.LogWarning("rejected");

        var normal = provider.CreateLogger("MessageService.Anything");
        normal.LogWarning("real problem");

        var snapshot = buffer.Snapshot();
        Assert.Single(snapshot);
        Assert.Equal("real problem", snapshot[0].Message);
    }
}
