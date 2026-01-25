# WatchStats

A lightweight, self-contained log monitoring and statistics tool that watches local directories for log file changes and computes real-time statistics.

## ⚠️ Platform Compatibility

**Important:** WatchStats has **limited Linux support** due to FileSystemWatcher limitations.

| Platform | Status | Notes |
|----------|--------|-------|
| Windows 10/11 | ✅ **Fully Supported** | Best compatibility |
| macOS | ✅ **Supported** | May need Full Disk Access for some directories |
| Linux | ⚠️ **Limited** | Requires inotify tuning, events may not fire |

**Linux users:** See [Platform Compatibility Guide](docs/platform-compatibility.md) for configuration instructions.

## Quick Start

### Prerequisites

- .NET 10.0 SDK or later
- Windows, macOS, or Linux (with inotify configured)

### Build

```bash
dotnet build --configuration Release
```

### Run

```bash
# Basic usage (Windows/macOS)
dotnet run --project WatchStats.Cli -- --dir /path/to/logs

# With options
dotnet run --project WatchStats.Cli -- \
  --dir /var/log/myapp \
  --workers 8 \
  --interval 5000 \
  --logLevel Information \
  --json-logs
```

### Command-Line Options

```
Required:
  --dir, --directory <path>    Directory to watch

Options:
  --workers <N>                Worker thread count (default: CPU count)
  --capacity <N>               Event bus capacity (default: 10000)
  --interval <ms>              Report interval in ms (default: 2000)
  --topk <N>                   Top-K message count (default: 10)
  --logLevel <level>           Minimum log level (default: Information)
  --json-logs                  Output logs in JSON format
  --no-metrics-logs            Disable periodic metrics logging
  -h, --help                   Show help message
```

### Environment Variables

All options can be set via environment variables:

```bash
export WATCHSTATS_DIRECTORY=/var/log/myapp
export WATCHSTATS_WORKERS=8
export WATCHSTATS_LOG_LEVEL=Debug
export WATCHSTATS_JSON_LOGS=1

dotnet run --project WatchStats.Cli
```

Per-category log levels:
```bash
export WATCHSTATS_LOG_LEVEL_WATCHER=Debug
export WATCHSTATS_LOG_LEVEL_REPORTER=Information
```

## Features

- **Real-time Monitoring**: Watches directories for file changes using FileSystemWatcher
- **Concurrent Processing**: Configurable worker threads process log files in parallel
- **Structured Logging**: JSON or text output with structured events and metrics
- **Performance Metrics**: Tracks throughput, latency percentiles, and GC statistics
- **Top-K Analysis**: Identifies most common log messages
- **Dependency Injection**: Clean composition with Microsoft.Extensions.DI
- **Observable**: Built-in structured logging and lifecycle events

## Architecture

- **WatchStats.Core**: Dependency-free core library with all business logic
- **WatchStats.Cli**: CLI host with DI composition and logging
- **WatchStats.Tests**: xUnit test suite

### Key Components

- `FilesystemWatcherAdapter`: Wraps FileSystemWatcher, publishes events to bus
- `BoundedEventBus<T>`: Thread-safe FIFO queue with drop-newest backpressure
- `ProcessingCoordinator`: Manages worker threads for parallel file processing
- `Reporter`: Collects and emits interval metrics and statistics
- `AppOrchestrator`: Manages component lifecycle with structured logging

## Testing

```bash
# Run all tests
dotnet test

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test category
dotnet test --filter "Category=Unit"
```

## Documentation

- [Project Specification](docs/project_specification.md) - High-level overview and goals
- [Technical Specification](docs/technical_specification.md) - Detailed design
- [Platform Compatibility](docs/platform-compatibility.md) - OS-specific notes and limitations
- [Observability](docs/observability/README.md) - DI and logging implementation
- [Thread Lifecycle](docs/thread_lifecycle.md) - Threading model details

## Linux Support Details

**FileSystemWatcher does not work reliably on Linux by default.** This is a known .NET limitation.

To use WatchStats on Linux:

1. **Increase inotify limits:**
   ```bash
   sudo sysctl fs.inotify.max_user_watches=524288
   sudo sysctl fs.inotify.max_user_instances=512
   ```

2. **Test that events are received:**
   ```bash
   mkdir -p /tmp/test-watch
   WatchStats --dir /tmp/test-watch --logLevel Debug &
   echo "test" > /tmp/test-watch/test.log
   # You should see "Worker batch processed" logs
   ```

3. **If events are still not received**, consider:
   - Using Windows or macOS instead
   - Implementing a polling-based alternative
   - Using a different file notification mechanism

See [Platform Compatibility Guide](docs/platform-compatibility.md) for detailed troubleshooting.

## Contributing

This is a learning/experimental project. Contributions welcome!

## License

[Specify license here]

## Acknowledgments

Built using:
- .NET 10.0
- Microsoft.Extensions.Logging
- xUnit for testing
