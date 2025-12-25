using System;
using System.Buffers;
using System.IO;

namespace WatchStats.Core
{
    public enum TailReadStatus
    {
        NoData,
        ReadSome,
        FileNotFound,
        AccessDenied,
        IoError,
        TruncatedReset
    }

    public sealed class FileTailer
    {
        private const int DefaultChunkSize = 64 * 1024;

        // Read newly appended bytes since offset. Does not advance offset unless bytesRead>0.
        // onChunk is invoked for each chunk read (span refers to rented buffer until callback returns).
        public TailReadStatus ReadAppended(
            string path,
            ref long offset,
            Action<ReadOnlySpan<byte>> onChunk,
            out int totalBytesRead,
            int chunkSize = DefaultChunkSize)
        {
            if (path == null) throw new ArgumentNullException(nameof(path));
            if (onChunk == null) throw new ArgumentNullException(nameof(onChunk));
            if (chunkSize <= 0) chunkSize = DefaultChunkSize;

            totalBytesRead = 0;
            bool truncated = false;

            byte[] buffer = null;
            try
            {
                // Open with sharing to allow writers to append and deletions/renames
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                long length;
                try
                {
                    length = fs.Length;
                }
                catch (IOException)
                {
                    // Could not obtain length
                    return TailReadStatus.IoError;
                }

                long effectiveOffset = offset;
                if (length < offset)
                {
                    // truncation detected
                    effectiveOffset = 0;
                    truncated = true;
                }

                if (effectiveOffset > length)
                {
                    // no data
                    if (truncated) return TailReadStatus.TruncatedReset;
                    return TailReadStatus.NoData;
                }

                if (effectiveOffset == length)
                {
                    // no new data
                    if (truncated) return TailReadStatus.TruncatedReset;
                    return TailReadStatus.NoData;
                }

                // seek to effectiveOffset
                fs.Seek(effectiveOffset, SeekOrigin.Begin);

                buffer = ArrayPool<byte>.Shared.Rent(chunkSize);

                int read;
                while ((read = fs.Read(buffer, 0, chunkSize)) > 0)
                {
                    totalBytesRead += read;
                    onChunk(new ReadOnlySpan<byte>(buffer, 0, read));
                }

                // advance offset only when we actually read bytes
                if (totalBytesRead > 0)
                {
                    offset = effectiveOffset + totalBytesRead;
                    return truncated ? TailReadStatus.TruncatedReset : TailReadStatus.ReadSome;
                }

                // If we didn't read (shouldn't reach here because effectiveOffset==length handled), handle fallthrough
                if (truncated) return TailReadStatus.TruncatedReset;
                return TailReadStatus.NoData;
            }
            catch (FileNotFoundException)
            {
                totalBytesRead = 0;
                return TailReadStatus.FileNotFound;
            }
            catch (DirectoryNotFoundException)
            {
                totalBytesRead = 0;
                return TailReadStatus.FileNotFound;
            }
            catch (UnauthorizedAccessException)
            {
                totalBytesRead = 0;
                return TailReadStatus.AccessDenied;
            }
            catch (IOException)
            {
                totalBytesRead = 0;
                return TailReadStatus.IoError;
            }
            finally
            {
                if (buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
    }
}