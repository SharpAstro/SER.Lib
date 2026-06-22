using System.Buffers.Binary;

namespace SharpAstro.Ser;

/// <summary>
/// Writes a SER planetary-video file: a placeholder header is reserved up front, frames are appended
/// in order, and on <see cref="Dispose"/> the header is rewritten (with the final frame count and
/// start time) followed by the optional per-frame timestamp trailer.
/// </summary>
/// <remarks>
/// Frame bytes are written verbatim, so the caller must supply host-order data; the
/// <c>LittleEndian</c> header flag is set to match the host under the Match-SER-Player convention so
/// a <see cref="SerReader"/> reads the data back identically.
/// </remarks>
public sealed class SerWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly SerColorId _colorId;
    private readonly int _width;
    private readonly int _height;
    private readonly int _pixelDepthPerPlane;
    private readonly int _luId;
    private readonly long _frameSize;
    private readonly string _observer;
    private readonly string _instrument;
    private readonly string _telescope;
    private readonly List<long> _timestampTicks = [];
    private bool? _timestamped;
    private int _frameCount;
    private bool _disposed;

    /// <summary>Creates a writer for a new SER file at <paramref name="path"/> (overwriting any existing file).</summary>
    public SerWriter(string path, int width, int height, SerColorId colorId, int pixelDepthPerPlane,
        string observer = "", string instrument = "", string telescope = "", int luId = 0)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixelDepthPerPlane is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelDepthPerPlane), pixelDepthPerPlane, "PixelDepthPerPlane must be in 1..16.");
        }

        _width = width;
        _height = height;
        _colorId = colorId;
        _pixelDepthPerPlane = pixelDepthPerPlane;
        _luId = luId;
        _observer = observer ?? "";
        _instrument = instrument ?? "";
        _telescope = telescope ?? "";

        var bytesPerSample = pixelDepthPerPlane <= 8 ? 1 : 2;
        _frameSize = (long)width * height * colorId.PlaneCount * bytesPerSample;

        _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> placeholder = stackalloc byte[SerHeader.Size];
        placeholder.Clear();
        _stream.Write(placeholder); // reserve the header; rewritten on Dispose
    }

    /// <summary>Bytes expected per <see cref="AppendFrame(ReadOnlySpan{byte})"/> call.</summary>
    public long FrameSizeBytes => _frameSize;

    /// <summary>Frames written so far.</summary>
    public int FrameCount => _frameCount;

    /// <summary>Appends one frame with no timestamp. All frames in a file must be consistent (all timestamped, or none).</summary>
    public void AppendFrame(ReadOnlySpan<byte> frame) => AppendFrameCore(frame, null);

    /// <summary>Appends one frame with a UTC timestamp (written to the trailer on close).</summary>
    public void AppendFrame(ReadOnlySpan<byte> frame, DateTimeOffset timestampUtc) => AppendFrameCore(frame, timestampUtc);

    private void AppendFrameCore(ReadOnlySpan<byte> frame, DateTimeOffset? timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Length != _frameSize)
        {
            throw new ArgumentException($"Frame must be exactly {_frameSize} bytes, got {frame.Length}.", nameof(frame));
        }

        var hasTimestamp = timestamp.HasValue;
        _timestamped ??= hasTimestamp;
        if (_timestamped.Value != hasTimestamp)
        {
            throw new InvalidOperationException("Provide a timestamp for every frame, or for none.");
        }

        _stream.Write(frame);
        if (hasTimestamp)
        {
            _timestampTicks.Add(timestamp!.Value.UtcDateTime.Ticks);
        }

        _frameCount++;
    }

    /// <summary>Finalises the file: writes the trailer (if timestamped) and rewrites the header.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        var writeTrailer = _timestamped == true && _frameCount > 0;
        long startTicks = 0;
        if (writeTrailer)
        {
            startTicks = _timestampTicks[0];
            Span<byte> tsBuffer = stackalloc byte[sizeof(long)];
            foreach (var ticks in _timestampTicks)
            {
                BinaryPrimitives.WriteInt64LittleEndian(tsBuffer, ticks);
                _stream.Write(tsBuffer);
            }
        }

        // Set both the local and UTC start time to the first frame's UTC ticks: the reader's
        // UTC-vs-local heuristic then resolves to UTC with zero correction, so timestamps round-trip
        // exactly. A non-zero DateTime is also what tells the reader a trailer is present.
        var header = SerHeader.Create(_colorId, _width, _height, _pixelDepthPerPlane, _frameCount,
            _observer, _instrument, _telescope,
            dateTimeTicks: startTicks, dateTimeUtcTicks: startTicks, luId: _luId);

        Span<byte> headerBytes = stackalloc byte[SerHeader.Size];
        header.Write(headerBytes);
        _stream.Position = 0;
        _stream.Write(headerBytes);
        _stream.Flush();
        _stream.Dispose();
    }
}
