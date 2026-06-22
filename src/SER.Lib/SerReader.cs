using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace SharpAstro.Ser;

/// <summary>
/// Reads a SER planetary-video file with memory-mapped, frame-accurate random access. The whole file
/// is mapped (not loaded); frame <c>N</c> is sliced directly from mapped memory at
/// <c>178 + N * FrameSizeBytes</c>, so seeking to any frame is O(1) even in multi-gigabyte files.
/// </summary>
public sealed unsafe class SerReader : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly byte* _basePtr;
    private readonly long _fileLength;
    private readonly long _framesEnd;
    // The per-frame timestamp trailer lives at the END of the file. Reading it during Open would fault
    // a page hundreds of MB in -- a full head-seek on a spinning disk, on EVERY open, purely to derive
    // the frame rate. It is read lazily instead, so browsing/scrubbing a folder of large files never
    // touches the file tail; only an explicit Timestamps/FramesPerSecond access (e.g. starting playback)
    // pays for it, once. Lazy<T> gives thread-safe single-init for the off-thread loader + render reads.
    private readonly Lazy<(ImmutableArray<DateTimeOffset> Timestamps, double? Fps)> _trailer;
    private bool _disposed;

    /// <summary>The parsed file header.</summary>
    public SerHeader Header { get; }

    /// <summary>
    /// Per-frame timestamps in UTC, or empty when the file carries no trailer (v2 files, or a file
    /// whose header start time is unset). Indexed by frame. Read lazily from the file trailer on first
    /// access -- see the <c>_trailer</c> field for why.
    /// </summary>
    public ImmutableArray<DateTimeOffset> Timestamps => _trailer.Value.Timestamps;

    /// <summary>Frame rate derived from the first/last timestamps, or null when unavailable. Triggers
    /// the lazy trailer read on first access.</summary>
    public double? FramesPerSecond => _trailer.Value.Fps;

    private SerReader(MemoryMappedFile mmf, MemoryMappedViewAccessor view, byte* basePtr,
        SerHeader header, long fileLength, long framesEnd)
    {
        _mmf = mmf;
        _view = view;
        _basePtr = basePtr;
        Header = header;
        _fileLength = fileLength;
        _framesEnd = framesEnd;
        _trailer = new Lazy<(ImmutableArray<DateTimeOffset> Timestamps, double? Fps)>(ReadTrailerLazily);
    }

    private (ImmutableArray<DateTimeOffset> Timestamps, double? Fps) ReadTrailerLazily()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var timestamps = ReadTrailer(_basePtr, Header, _fileLength, _framesEnd);
        return (timestamps, ComputeFps(timestamps));
    }

    /// <summary>Frame width in pixels.</summary>
    public int Width => Header.Width;
    /// <summary>Frame height in pixels.</summary>
    public int Height => Header.Height;
    /// <summary>Number of frames.</summary>
    public int FrameCount => Header.FrameCount;
    /// <summary>Colour mode.</summary>
    public SerColorId ColorId => Header.ColorId;
    /// <summary>True bit depth per plane.</summary>
    public int PixelDepthPerPlane => Header.PixelDepthPerPlane;
    /// <summary>Bytes per sample (1 or 2).</summary>
    public int BytesPerSample => Header.BytesPerSample;
    /// <summary>Colour planes per pixel (1 or 3).</summary>
    public int PlaneCount => Header.PlaneCount;
    /// <summary>Maximum sample value for the declared bit depth.</summary>
    public int MaxSampleValue => Header.MaxSampleValue;
    /// <summary>Samples in one frame: <c>Width * Height * PlaneCount</c>.</summary>
    public int SamplesPerFrame => Header.Width * Header.Height * Header.PlaneCount;
    /// <summary>Bytes in one frame.</summary>
    public long FrameSizeBytes => Header.FrameSizeBytes;
    /// <summary>Whether per-frame timestamps are present.</summary>
    public bool HasTimestamps => !Timestamps.IsDefaultOrEmpty;

    /// <summary>Opens <paramref name="path"/> for reading.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidDataException">The file is too small, truncated, or has invalid dimensions.</exception>
    public static SerReader Open(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("SER file not found.", path);
        }

        var length = info.Length;
        if (length < SerHeader.Size)
        {
            throw new InvalidDataException($"File '{path}' is too small ({length} bytes) to be a SER file.");
        }

        var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor? view = null;
        byte* basePtr = null;
        var acquired = false;
        try
        {
            view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
            acquired = true;
            basePtr += view.PointerOffset;

            var header = SerHeader.Parse(new ReadOnlySpan<byte>(basePtr, SerHeader.Size));
            if (header.Width <= 0 || header.Height <= 0)
            {
                throw new InvalidDataException($"SER header reports invalid dimensions {header.Width}x{header.Height}.");
            }

            if (header.FrameCount < 0)
            {
                throw new InvalidDataException($"SER header reports a negative frame count ({header.FrameCount}).");
            }

            var frameSize = header.FrameSizeBytes;
            var framesEnd = checked(SerHeader.Size + frameSize * header.FrameCount);
            if (framesEnd > length)
            {
                throw new InvalidDataException(
                    $"SER file '{path}' is truncated: {header.FrameCount} frame(s) x {frameSize} bytes plus the 178-byte header exceed the {length}-byte file.");
            }

            // NB: the per-frame timestamp trailer (at the END of the file) is deliberately NOT read
            // here -- it is read lazily on first Timestamps/FramesPerSecond access (see the _trailer
            // field). Open only parses the 178-byte header already mapped at offset 0, so opening a
            // multi-gigabyte file off a spinning disk does not seek to the file tail.
            var reader = new SerReader(mmf, view, basePtr, header, length, framesEnd);
            view = null; // ownership transferred to the reader
            return reader;
        }
        catch
        {
            if (acquired)
            {
                view!.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            view?.Dispose();
            mmf.Dispose();
            throw;
        }
    }

    /// <summary>Copies the raw bytes of frame <paramref name="index"/> (length must equal <see cref="FrameSizeBytes"/>).</summary>
    public void ReadFrameBytes(int index, Span<byte> destination)
    {
        var frameSize = ValidateFrameIndex(index);
        if (destination.Length != frameSize)
        {
            throw new ArgumentException($"Destination must be exactly {frameSize} bytes (one frame), got {destination.Length}.", nameof(destination));
        }

        FrameSpan(index, frameSize).CopyTo(destination);
    }

    /// <summary>
    /// Decodes frame <paramref name="index"/> into 16-bit samples (length must equal
    /// <see cref="SamplesPerFrame"/>). 8-bit data is widened to 0..255; 16-bit data is byte-swapped
    /// as needed so the result is always host-order. Values are the raw sample range
    /// (<c>0..</c><see cref="MaxSampleValue"/>); normalise by <see cref="MaxSampleValue"/> for display.
    /// </summary>
    public void ReadFrame16(int index, Span<ushort> destination)
    {
        var frameSize = ValidateFrameIndex(index);
        var samples = SamplesPerFrame;
        if (destination.Length != samples)
        {
            throw new ArgumentException($"Destination must be exactly {samples} samples (one frame), got {destination.Length}.", nameof(destination));
        }

        var src = FrameSpan(index, frameSize);
        if (Header.BytesPerSample == 1)
        {
            for (var i = 0; i < samples; i++)
            {
                destination[i] = src[i];
            }

            return;
        }

        var src16 = MemoryMarshal.Cast<byte, ushort>(src);
        if (Header.DataLittleEndian == BitConverter.IsLittleEndian)
        {
            src16[..samples].CopyTo(destination);
        }
        else
        {
            for (var i = 0; i < samples; i++)
            {
                destination[i] = BinaryPrimitives.ReverseEndianness(src16[i]);
            }
        }
    }

    private long ValidateFrameIndex(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)Header.FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"Frame index must be in [0, {Header.FrameCount}).");
        }

        return Header.FrameSizeBytes;
    }

    private ReadOnlySpan<byte> FrameSpan(int index, long frameSize)
    {
        var offset = SerHeader.Size + index * frameSize;
        return new ReadOnlySpan<byte>(_basePtr + offset, checked((int)frameSize));
    }

    private static ImmutableArray<DateTimeOffset> ReadTrailer(byte* basePtr, in SerHeader header, long fileLength, long framesEnd)
    {
        // The trailer is present only when the header carries a start time and the file is large
        // enough to hold FrameCount 64-bit timestamps after the frame data.
        if (header.DateTimeTicks <= 0 || header.FrameCount <= 0)
        {
            return ImmutableArray<DateTimeOffset>.Empty;
        }

        var trailerBytes = (long)header.FrameCount * sizeof(long);
        if (framesEnd + trailerBytes > fileLength)
        {
            return ImmutableArray<DateTimeOffset>.Empty;
        }

        var raw = new long[header.FrameCount];
        var trailer = new ReadOnlySpan<byte>(basePtr + framesEnd, checked((int)trailerBytes));
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = BinaryPrimitives.ReadInt64LittleEndian(trailer[(i * sizeof(long))..]);
        }

        // UTC-vs-local detection (mirrors SER Player): if the header's local start time is closer to
        // the earliest frame timestamp than the UTC start time is, the trailer is in local time, so
        // shift every timestamp by (UTC - local) to normalise to UTC.
        long correction = 0;
        if (header.DateTimeUtcTicks > 0)
        {
            var minTs = long.MaxValue;
            foreach (var t in raw)
            {
                if (t < minTs)
                {
                    minTs = t;
                }
            }

            var utcDistance = Math.Abs(header.DateTimeUtcTicks - minTs);
            var localDistance = Math.Abs(header.DateTimeTicks - minTs);
            if (localDistance < utcDistance)
            {
                correction = header.DateTimeUtcTicks - header.DateTimeTicks;
            }
        }

        var builder = ImmutableArray.CreateBuilder<DateTimeOffset>(raw.Length);
        foreach (var t in raw)
        {
            builder.Add(SerTimestamp.FromTicks(t + correction));
        }

        return builder.MoveToImmutable();
    }

    private static double? ComputeFps(ImmutableArray<DateTimeOffset> timestamps)
    {
        if (timestamps.IsDefaultOrEmpty || timestamps.Length < 2)
        {
            return null;
        }

        var span = timestamps[^1] - timestamps[0];
        return span > TimeSpan.Zero ? (timestamps.Length - 1) / span.TotalSeconds : null;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
    }
}
