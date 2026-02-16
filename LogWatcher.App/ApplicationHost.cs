using LogWatcher.Core.Concurrency;
using LogWatcher.Core.Events;
using LogWatcher.Core.IO;
using LogWatcher.Core.Metrics;
using LogWatcher.Core.Processing;

namespace LogWatcher.App;

/// <summary>
/// Hosts and executes the LogWatcher application with validated configuration.
/// Responsible for component wiring, startup, and shutdown coordination.
/// </summary>
public static class ApplicationHost
{
    /// <summary>
    /// Runs the application with the provided configuration.
    /// </summary>
    /// <param name="config">Validated CLI configuration.</param>
    /// <returns>Exit code: 0 for success, 1 for runtime error.</returns>
    public static int Run(CliConfig config)
    {
        BoundedEventBus<FsEvent>? bus = null;
        FileStateRegistry? registry = null;
        FileTailer? tailer = null;
        FileProcessor? processor = null;
        WorkerStats[]? workerStats = null;
        ProcessingCoordinator? coordinator = null;
        Reporter? reporter = null;
        FilesystemWatcherAdapter? watcher = null;

        try
        {
            // Construct components
            bus = new BoundedEventBus<FsEvent>(config.QueueCapacity);
            registry = new FileStateRegistry();
            tailer = new FileTailer();
            processor = new FileProcessor(tailer);

            workerStats = new WorkerStats[config.Workers];
            for (int i = 0; i < workerStats.Length; i++)
            {
                workerStats[i] = new WorkerStats();
            }

            coordinator = new ProcessingCoordinator(bus, registry, processor, workerStats, 
                workerCount: config.Workers);
            reporter = new Reporter(workerStats, bus, config.TopK, config.ReportIntervalSeconds);
            watcher = new FilesystemWatcherAdapter(config.WatchPath, bus);

            // Register shutdown handlers
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                HostWiring.TriggerShutdown(bus, watcher, coordinator, reporter);
            };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => 
                HostWiring.TriggerShutdown(bus, watcher, coordinator, reporter);

            // Start components in order
            coordinator.Start();
            reporter.Start();
            watcher.Start();

            Console.WriteLine("Started: " + config);

            // Wait until shutdown is requested
            HostWiring.WaitForShutdown();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex}");
            return 1;
        }
        finally
        {
            // Ensure final cleanup
            HostWiring.TriggerShutdown(bus, watcher, coordinator, reporter);
        }
    }
}
