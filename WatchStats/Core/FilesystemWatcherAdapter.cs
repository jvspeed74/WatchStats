using System;
using System.IO;

namespace WatchStats.Core
{
    public sealed class FilesystemWatcherAdapter : IDisposable
    {
        private readonly string _path;
        private readonly BoundedEventBus<FsEvent> _bus;
        private readonly Func<string, bool> _isProcessable;
        private FileSystemWatcher? _watcher;
        private long _errorCount;

        public FilesystemWatcherAdapter(string path, BoundedEventBus<FsEvent> bus, Func<string, bool>? isProcessable = null)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _bus = bus ?? throw new ArgumentNullException(nameof(bus));
            _isProcessable = isProcessable ?? DefaultIsProcessable;

            // Pre-create watcher but do not enable until Start()
            _watcher = new FileSystemWatcher(_path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*.*",
                InternalBufferSize = 64 * 1024
            };

            _watcher.Created += OnCreated;
            _watcher.Changed += OnChanged;
            _watcher.Deleted += OnDeleted;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
        }

        public long ErrorCount => System.Threading.Interlocked.Read(ref _errorCount);

        public void Start()
        {
            if (_watcher == null) throw new ObjectDisposedException(nameof(FilesystemWatcherAdapter));
            _watcher.EnableRaisingEvents = true;
        }

        public void Stop()
        {
            if (_watcher == null) return;
            _watcher.EnableRaisingEvents = false;
        }

        private bool DefaultIsProcessable(string path)
        {
            var ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            ext = ext.TrimStart('.');
            return string.Equals(ext, "log", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, "txt", StringComparison.OrdinalIgnoreCase);
        }

        private void OnCreated(object sender, FileSystemEventArgs e)
        {
            PublishEvent(FsEventKind.Created, e.FullPath, null);
        }

        private void OnChanged(object sender, FileSystemEventArgs e)
        {
            PublishEvent(FsEventKind.Modified, e.FullPath, null);
        }

        private void OnDeleted(object sender, FileSystemEventArgs e)
        {
            PublishEvent(FsEventKind.Deleted, e.FullPath, null);
        }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            PublishEvent(FsEventKind.Renamed, e.FullPath, e.OldFullPath);
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            System.Threading.Interlocked.Increment(ref _errorCount);
            // do not rethrow; just record
        }

        private void PublishEvent(FsEventKind kind, string path, string? oldPath)
        {
            try
            {
                bool processable = _isProcessable(path);
                var ev = new FsEvent(kind, path, oldPath, DateTimeOffset.UtcNow, processable);
                _bus.Publish(ev);
            }
            catch
            {
                // Swallow any exceptions to keep handlers lightweight
            }
        }

        public void Dispose()
        {
            if (_watcher != null)
            {
                Stop();
                _watcher.Created -= OnCreated;
                _watcher.Changed -= OnChanged;
                _watcher.Deleted -= OnDeleted;
                _watcher.Renamed -= OnRenamed;
                _watcher.Error -= OnError;
                _watcher.Dispose();
                _watcher = null;
            }
        }
    }
}

