using System;

namespace WatchStats.Core
{
    public readonly record struct FsEvent(
        FsEventKind Kind,
        string Path,
        string? OldPath,
        DateTimeOffset ObservedAt,
        bool Processable);
}