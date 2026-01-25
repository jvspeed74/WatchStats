# Platform Compatibility Notes

## Linux Compatibility Issues

### FileSystemWatcher Limitations

WatchStats uses .NET's `FileSystemWatcher` which has **known compatibility issues on Linux**:

#### Problem
**Events are not received reliably on Linux systems**, causing the application to fail to detect file changes.

#### Root Causes

1. **inotify Watch Limits**: Linux uses inotify for file watching, which has a default limit on the number of watched files:
   ```bash
   # Check current limit
   cat /proc/sys/fs/inotify/max_user_watches
   
   # Typical default: 8192 (often insufficient)
   ```

2. **FileSystemWatcher Implementation**: .NET's FileSystemWatcher on Linux relies on inotify watches, which may not fire events as expected for certain file operations.

3. **Notification Filter Incompatibility**: Some `NotifyFilter` combinations don't work consistently across platforms.

#### Solutions

##### Option 1: Increase inotify Limits (Recommended for Production)

Temporarily:
```bash
sudo sysctl fs.inotify.max_user_watches=524288
```

Permanently (add to `/etc/sysctl.conf`):
```bash
fs.inotify.max_user_watches=524288
fs.inotify.max_user_instances=512
```

Then reload:
```bash
sudo sysctl -p
```

##### Option 2: Use Windows or macOS

WatchStats works reliably on:
- ✅ **Windows 10/11** - Native FileSystemWatcher support
- ✅ **macOS** - Uses FSEvents, generally reliable
- ⚠️ **Linux** - Limited support, may require tuning

##### Option 3: Manual File Polling (Alternative Approach)

For production Linux deployments, consider:
- Using a cron job to trigger processing
- Implementing a custom polling-based file monitor
- Using a queue-based architecture instead of filesystem watching

#### Warning Messages

When running on Linux, WatchStats logs:
```
[Warning] platform_warning: Running on Linux: FileSystemWatcher may have limited functionality. 
Events may not be received reliably. Consider increasing inotify limits 
(/proc/sys/fs/inotify/max_user_watches) or using polling mode.
```

#### Testing on Linux

To verify FileSystemWatcher works on your Linux system:

```bash
# 1. Create test directory
mkdir -p /tmp/watchstats-test

# 2. Run WatchStats with debug logging
WatchStats --dir /tmp/watchstats-test --logLevel Debug

# 3. In another terminal, create/modify files
echo "test log line" > /tmp/watchstats-test/test.log
echo "another line" >> /tmp/watchstats-test/test.log

# 4. Check if events are logged in WatchStats output
# You should see: "Worker batch processed" or similar logs
```

If no events are detected, FileSystemWatcher is not working on your system.

#### Future Enhancements

Potential improvements for Linux support:
- [ ] Add `--polling-mode` flag to use timer-based polling instead of inotify
- [ ] Implement custom inotify wrapper for more control
- [ ] Add health check that detects when events aren't firing
- [ ] Support `--max-files` option to limit watch count

## Other Platform Notes

### macOS
- Generally works well with FSEvents
- May have permission issues with protected directories (requires Full Disk Access)

### Windows
- Fully supported
- May need to run as Administrator for system directories
- Antivirus software can interfere with file events

## Recommendations by Platform

| Platform | Recommendation | Notes |
|----------|---------------|-------|
| **Windows** | ✅ Use directly | Best compatibility |
| **macOS** | ✅ Use directly | Grant Full Disk Access if needed |
| **Linux** | ⚠️ Increase inotify limits first | Test thoroughly before production use |
| **Docker (Linux)** | ⚠️ May not work | Consider volume mount alternatives |

## Getting Help

If you encounter platform-specific issues:

1. Check the warning logs from `FilesystemWatcherAdapter`
2. Verify inotify limits on Linux: `cat /proc/sys/fs/inotify/max_user_watches`
3. Test with `--logLevel Debug` to see detailed event flow
4. Open an issue with:
   - Platform/OS version
   - Output of `dotnet --info`
   - Log output with `--json-logs` enabled
