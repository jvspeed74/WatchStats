using Microsoft.Extensions.Logging;
using System.Text.Json;
using WatchStats.Core.Concurrency;
using WatchStats.Core.Events;
using WatchStats.Core.Metrics;
using ParsingLogLevel = WatchStats.Core.Processing.LogLevel;

namespace WatchStats.Tests.Integration;

/// <summary>
/// Tests to verify Reporter's structured logging implementation for US-203.
/// </summary>
public class ReporterStructuredLoggingTests
{
    private class TestLoggerProvider : ILoggerProvider
    {
        public List<LogEntry> Logs { get; } = new();

        public ILogger CreateLogger(string categoryName)
        {
            return new TestLogger(this, categoryName);
        }

        public void Dispose() { }

        private class TestLogger : ILogger
        {
            private readonly TestLoggerProvider _provider;
            private readonly string _categoryName;

            public TestLogger(TestLoggerProvider provider, string categoryName)
            {
                _provider = provider;
                _categoryName = categoryName;
            }

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var stateDict = state as IReadOnlyList<KeyValuePair<string, object?>>;
                _provider.Logs.Add(new LogEntry
                {
                    LogLevel = logLevel,
                    EventId = eventId,
                    Message = formatter(state, exception),
                    State = stateDict?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value) ?? new Dictionary<string, object?>()
                });
            }
        }
    }

    private class LogEntry
    {
        public LogLevel LogLevel { get; set; }
        public EventId EventId { get; set; }
        public string Message { get; set; } = string.Empty;
        public Dictionary<string, object?> State { get; set; } = new();
    }

    [Fact]
    public void Reporter_EmitsCorrectStructuredMetrics_WithAllRequiredFields()
    {
        // Arrange
        var logProvider = new TestLoggerProvider();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logProvider));
        var logger = loggerFactory.CreateLogger<Reporter>();

        var bus = new BoundedEventBus<FsEvent>(100);
        var workers = new WorkerStats[1];
        workers[0] = new WorkerStats();

        // Populate with known test data
        workers[0].Active.LinesProcessed = 15000;
        workers[0].Active.MalformedLines = 10;
        workers[0].Active.TruncationResetCount = 2;
        workers[0].Active.IncrementLevel(ParsingLogLevel.Info);
        workers[0].Active.IncrementLevel(ParsingLogLevel.Info);
        workers[0].Active.IncrementLevel(ParsingLogLevel.Warn);
        workers[0].Active.IncrementLevel(ParsingLogLevel.Error);
        workers[0].Active.RecordLatency(50);
        workers[0].Active.RecordLatency(100);
        workers[0].Active.RecordLatency(150);

        workers[0].RequestSwap();
        workers[0].AcknowledgeSwapIfRequested();

        var reporter = new Reporter(workers, bus, 5, 2000, true, logger);

        // Act
        var snapshot = reporter.BuildSnapshotAndFrame();
        // Manually invoke metrics emission (simulating what the reporter loop would do)
        var emitMethod = typeof(Reporter).GetMethod("EmitMetrics", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        emitMethod?.Invoke(reporter, new object[] { snapshot, 2000 });

        // Assert
        var metricsLog = logProvider.Logs.FirstOrDefault(l => l.EventId.Name == "reporter_interval");
        Assert.NotNull(metricsLog);
        Assert.Equal(LogLevel.Information, metricsLog.LogLevel);

        // Verify all required fields are present
        var state = metricsLog.State;
        Assert.True(state.ContainsKey("IntervalMs"));
        Assert.True(state.ContainsKey("Lines"));
        Assert.True(state.ContainsKey("LinesPerSec"));
        Assert.True(state.ContainsKey("Malformed"));
        Assert.True(state.ContainsKey("MalformedPerSec"));
        Assert.True(state.ContainsKey("P50"));
        Assert.True(state.ContainsKey("P95"));
        Assert.True(state.ContainsKey("P99"));
        Assert.True(state.ContainsKey("Drops"));
        Assert.True(state.ContainsKey("Truncations"));
        Assert.True(state.ContainsKey("Overflows"));
        Assert.True(state.ContainsKey("Gc0"));
        Assert.True(state.ContainsKey("Gc1"));
        Assert.True(state.ContainsKey("Gc2"));
        Assert.True(state.ContainsKey("LevelInfo"));
        Assert.True(state.ContainsKey("LevelWarn"));
        Assert.True(state.ContainsKey("LevelError"));
        Assert.True(state.ContainsKey("LevelOther"));

        // Verify computed values
        Assert.Equal(2000, state["IntervalMs"]);
        Assert.Equal(15000L, state["Lines"]);
        Assert.Equal(10L, state["Malformed"]);
        Assert.Equal(2L, state["Truncations"]);
        Assert.Equal(2L, state["LevelInfo"]);
        Assert.Equal(1L, state["LevelWarn"]);
        Assert.Equal(1L, state["LevelError"]);

        // Verify rate calculations
        var linesPerSec = (double)state["LinesPerSec"]!;
        var malformedPerSec = (double)state["MalformedPerSec"]!;
        Assert.Equal(7500.0, linesPerSec, 0.1); // 15000 / 2 seconds
        Assert.Equal(5.0, malformedPerSec, 0.1); // 10 / 2 seconds
    }

    [Fact]
    public void Reporter_HandlesZeroLines_WithoutNaN()
    {
        // Arrange
        var logProvider = new TestLoggerProvider();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logProvider));
        var logger = loggerFactory.CreateLogger<Reporter>();

        var bus = new BoundedEventBus<FsEvent>(100);
        var workers = new WorkerStats[1];
        workers[0] = new WorkerStats();

        // Don't add any lines - test zero-line scenario
        workers[0].RequestSwap();
        workers[0].AcknowledgeSwapIfRequested();

        var reporter = new Reporter(workers, bus, 5, 2000, true, logger);

        // Act
        var snapshot = reporter.BuildSnapshotAndFrame();
        var emitMethod = typeof(Reporter).GetMethod("EmitMetrics", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        emitMethod?.Invoke(reporter, new object[] { snapshot, 2000 });

        // Assert
        var metricsLog = logProvider.Logs.FirstOrDefault(l => l.EventId.Name == "reporter_interval");
        Assert.NotNull(metricsLog);

        var state = metricsLog.State;
        Assert.Equal(0L, state["Lines"]);
        Assert.Equal(0L, state["Malformed"]);

        // Verify rates are 0.0, not NaN
        var linesPerSec = (double)state["LinesPerSec"]!;
        var malformedPerSec = (double)state["MalformedPerSec"]!;
        Assert.Equal(0.0, linesPerSec);
        Assert.Equal(0.0, malformedPerSec);
        Assert.False(double.IsNaN(linesPerSec));
        Assert.False(double.IsNaN(malformedPerSec));
    }

    [Fact]
    public void Reporter_DoesNotEmitLogs_WhenMetricsLoggingDisabled()
    {
        // Arrange
        var logProvider = new TestLoggerProvider();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logProvider));
        var logger = loggerFactory.CreateLogger<Reporter>();

        var bus = new BoundedEventBus<FsEvent>(100);
        var workers = new WorkerStats[1];
        workers[0] = new WorkerStats();

        workers[0].Active.LinesProcessed = 1000;
        workers[0].RequestSwap();
        workers[0].AcknowledgeSwapIfRequested();

        // Act - Create reporter with metrics logging DISABLED
        var reporter = new Reporter(workers, bus, 5, 2000, false, logger);
        var snapshot = reporter.BuildSnapshotAndFrame();
        var emitMethod = typeof(Reporter).GetMethod("EmitMetrics", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        emitMethod?.Invoke(reporter, new object[] { snapshot, 2000 });

        // Assert - No metrics logs should be emitted
        var metricsLog = logProvider.Logs.FirstOrDefault(l => l.EventId.Name == "reporter_interval");
        Assert.Null(metricsLog);
    }

    [Fact]
    public void Reporter_CalculatesRatesUsingMeasuredInterval()
    {
        // Arrange
        var logProvider = new TestLoggerProvider();
        var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(logProvider));
        var logger = loggerFactory.CreateLogger<Reporter>();

        var bus = new BoundedEventBus<FsEvent>(100);
        var workers = new WorkerStats[1];
        workers[0] = new WorkerStats();

        workers[0].Active.LinesProcessed = 10000;
        workers[0].Active.MalformedLines = 100;
        workers[0].RequestSwap();
        workers[0].AcknowledgeSwapIfRequested();

        var reporter = new Reporter(workers, bus, 5, 2000, true, logger);

        // Act - Use a different measured interval (2345ms) vs configured (2000ms)
        var snapshot = reporter.BuildSnapshotAndFrame();
        var emitMethod = typeof(Reporter).GetMethod("EmitMetrics", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        emitMethod?.Invoke(reporter, new object[] { snapshot, 2345 });

        // Assert
        var metricsLog = logProvider.Logs.FirstOrDefault(l => l.EventId.Name == "reporter_interval");
        Assert.NotNull(metricsLog);

        var state = metricsLog.State;
        Assert.Equal(2345, state["IntervalMs"]); // Should use measured interval

        // Verify rates are calculated using measured interval, not configured
        var linesPerSec = (double)state["LinesPerSec"]!;
        var malformedPerSec = (double)state["MalformedPerSec"]!;
        
        // Expected: 10000 / (2345/1000) = 4264.39
        Assert.Equal(4264.39, linesPerSec, 0.1);
        // Expected: 100 / (2345/1000) = 42.64
        Assert.Equal(42.64, malformedPerSec, 0.1);
    }
}
