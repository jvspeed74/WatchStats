using System;
using System.Threading;
using WatchStats.Core;

namespace WatchStats
{
    internal static class HostWiring
    {
        private static int _shutdownRequested = 0;
        private static readonly ManualResetEventSlim _shutdownEvent = new(false);

        public static void TriggerShutdown(BoundedEventBus<FsEvent>? bus, FilesystemWatcherAdapter? watcher,
            ProcessingCoordinator? coordinator, Reporter? reporter)
        {
            if (Interlocked.Exchange(ref _shutdownRequested, 1) == 1) return;
            Console.WriteLine("Shutdown requested...");

            try
            {
                if (watcher != null)
                {
                    try
                    {
                        watcher.Stop();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"watcher.Stop error: {ex}");
                    }
                }

                if (bus != null)
                {
                    try
                    {
                        bus.Stop();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"bus.Stop error: {ex}");
                    }
                }

                if (coordinator != null)
                {
                    try
                    {
                        coordinator.Stop();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"coordinator.Stop error: {ex}");
                    }
                }

                if (reporter != null)
                {
                    try
                    {
                        reporter.Stop();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"reporter.Stop error: {ex}");
                    }
                }

                if (watcher != null)
                {
                    try
                    {
                        watcher.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"watcher.Dispose error: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error during shutdown: {ex}");
            }
            finally
            {
                _shutdownEvent.Set();
            }
        }

        public static void WaitForShutdown() => _shutdownEvent.Wait();
    }
}