using System;

namespace WatchStats.Core
{
    // PartialLineBuffer holds carryover bytes for a single file between chunk scans.
    // Fields are public to match the doc's simple data-shape.
    public struct PartialLineBuffer
    {
        // underlying buffer
        public byte[] Buffer;

        // number of valid bytes in Buffer (0..Buffer.Length)
        public int Length;

        private const int InitialSize = 256;

        // Return a readonly span of the valid bytes
        public ReadOnlySpan<byte> AsSpan()
        {
            if (Buffer == null || Length == 0)
                return ReadOnlySpan<byte>.Empty;
            return new ReadOnlySpan<byte>(Buffer, 0, Length);
        }

        // Clear the buffer (does not free the array)
        public void Clear()
        {
            Length = 0;
        }

        // Append src into the buffer, growing by doubling when needed.
        public void Append(ReadOnlySpan<byte> src)
        {
            if (src.Length == 0)
                return;

            if (Buffer == null)
            {
                Buffer = new byte[InitialSize];
            }

            int required = Length + src.Length;
            if (required > Buffer.Length)
            {
                int newSize = Buffer.Length;
                while (newSize < required)
                {
                    newSize = Math.Max(newSize * 2, newSize + 1);
                }

                var newBuf = new byte[newSize];
                if (Length > 0)
                    Array.Copy(Buffer, 0, newBuf, 0, Length);
                Buffer = newBuf;
            }

            // copy src into Buffer at offset Length
            src.CopyTo(new Span<byte>(Buffer, Length, src.Length));
            Length = required;
        }
    }

    public static class Utf8LineScanner
    {
        // Scan processes `carry + chunk` and invokes onLine for each complete line (without newline, and trimming CR).
        // Any trailing incomplete bytes are saved back into `carry`.
        public static void Scan(ReadOnlySpan<byte> chunk, ref PartialLineBuffer carry,
            Action<ReadOnlySpan<byte>> onLine)
        {
            if (onLine == null) throw new ArgumentNullException(nameof(onLine));

            // Handle the carry path first
            if (carry.Length > 0)
            {
                // find first '\n' in chunk
                int nlIndex = -1;
                for (int i = 0; i < chunk.Length; i++)
                {
                    if (chunk[i] == (byte)'\n')
                    {
                        nlIndex = i;
                        break;
                    }
                }

                if (nlIndex == -1)
                {
                    // no newline in this chunk -> append whole chunk to carry and return
                    carry.Append(chunk);
                    return;
                }

                // newline found at nlIndex -> append chunk[..nlIndex] to carry, emit carry as a single line
                if (nlIndex > 0)
                {
                    carry.Append(chunk.Slice(0, nlIndex));
                }
                // else nlIndex == 0 -> nothing to append, carry already contains prior bytes

                var emitSpan = carry.AsSpan();
                if (emitSpan.Length > 0 && emitSpan[emitSpan.Length - 1] == (byte)'\r')
                {
                    emitSpan = emitSpan.Slice(0, emitSpan.Length - 1);
                }

                onLine(emitSpan);
                carry.Clear();

                // continue scanning with the remainder of the chunk after the newline
                chunk = chunk.Slice(nlIndex + 1);
            }

            // Scan remaining chunk for newline delimiters
            int start = 0;
            while (start < chunk.Length)
            {
                int j = -1;
                for (int i = start; i < chunk.Length; i++)
                {
                    if (chunk[i] == (byte)'\n')
                    {
                        j = i;
                        break;
                    }
                }

                if (j == -1)
                    break; // no more newlines

                var lineSpan = chunk.Slice(start, j - start);
                if (lineSpan.Length > 0 && lineSpan[lineSpan.Length - 1] == (byte)'\r')
                {
                    lineSpan = lineSpan.Slice(0, lineSpan.Length - 1);
                }

                onLine(lineSpan);
                start = j + 1;
            }

            // store trailing bytes (if any) into carry
            if (start < chunk.Length)
            {
                carry.Append(chunk.Slice(start));
            }
        }
    }
}