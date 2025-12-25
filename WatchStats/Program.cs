using System;
using WatchStats;
using WatchStats.Core;

// Host wiring for WatchStats

int exitCode = 0;

if (!CliParser.TryParse(args, out var config, out var parseError))
{
    if (parseError == "help")
    {
        Console.WriteLine(
            "Usage: WatchStats <watchPath> [--workers N] [--queue-capacity N] [--report-interval-seconds N] [--topk N]");
        return;
    }

    Console.Error.WriteLine("Invalid arguments: " + parseError);
    Environment.Exit(2);
}

BoundedEventBus<FsEvent>? bus = null;
FileStateRegistry? registry = null;
FileTailer? tailer = null;
FileProcessor? processor = null;
WorkerStats[]? workerStats = null;
ProcessingCoordinator? coordinator = null;
WatchStats.Core.Reporter? reporter = null;
FilesystemWatcherAdapter? watcher = null;

try
{
    // construct components
    bus = new BoundedEventBus<FsEvent>(config.QueueCapacity);
    registry = new FileStateRegistry();
    tailer = new FileTailer();
    processor = new FileProcessor(tailer);

    workerStats = new WorkerStats[config.Workers];
    for (int i = 0; i < workerStats.Length; i++)
    {
        workerStats[i] = new WorkerStats();
    }

    coordinator = new ProcessingCoordinator(bus, registry, processor, workerStats, workerCount: config.Workers);
    reporter = new WatchStats.Core.Reporter(workerStats, bus, config.TopK, config.ReportIntervalSeconds);
    watcher = new FilesystemWatcherAdapter(config.WatchPath, bus);

    // register shutdown handlers
    Console.CancelKeyPress += (s, e) =>
    {
        e.Cancel = true;
        HostWiring.TriggerShutdown(bus, watcher, coordinator, reporter);
    };
    AppDomain.CurrentDomain.ProcessExit += (s, e) => HostWiring.TriggerShutdown(bus, watcher, coordinator, reporter);

    // start components in order
    coordinator.Start();
    reporter.Start();
    watcher.Start();

    Console.WriteLine("Started: " + config.ToString());

    // wait until shutdown is requested
    HostWiring.WaitForShutdown();
    exitCode = 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Fatal error during startup: " + ex);
    exitCode = 1;
}
finally
{
    // ensure final cleanup if shutdown not already requested
    HostWiring.TriggerShutdown(bus, watcher, coordinator, reporter);
}

Environment.Exit(exitCode);