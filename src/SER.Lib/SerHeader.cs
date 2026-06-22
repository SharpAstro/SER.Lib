using System.Buffers.Binary;
using System.Text;

namespace SharpAstro.Ser;

/// <summary>
/// The fixed 178-byte SER file header (spec v3). All multi-byte fields are stored little-endian
/// <i>regardless</i> of the <see cref="LittleEndianFlag"/> -- that flag only governs the byte order
/// of 16-bit <i>pixel</i> data (see <see cref="DataLittleEndian"/>).
/// </summary>
/// <remarks>
/// Layout (offset : size : field): 0:14 FileID "LUCAM-RECORDER" · 14:4 LuID · 18:4 ColorID ·
/// 22:4 LittleEndian · 26:4 ImageWidth · 30:4 ImageHeight · 34:4 PixelDepthPerPlane ·
/// 38:4 FrameCount · 42:40 Observer · 82:40 Instrument · 122:40 Telescope · 162:8 DateTime (local) ·
/// 170:8 DateTime_UTC.
/// </remarks>
public readonly struct SerHeader
{
    /// <summary>Total header size in bytes.</summary>
    public const int Size = 178;

    /// <summary>The canonical 14-byte file signature.</summary>
    public const string FileIdValue = "LUCAM-RECORDER";

    private const int StringFieldLength = 40;

    /// <summary>File signature as read (normally <see cref="FileIdValue"/>); not validated on read.</summary>
    public string FileId { get; }
    /// <summary>Lumenera camera series ID (usually 0; informational).</summary>
    public int LuId { get; }
    /// <summary>Colour mode.</summary>
    public SerColorId ColorId { get; }
    /// <summary>Raw <c>LittleEndian</c> header flag (0 or 1). See <see cref="DataLittleEndian"/> for its meaning.</summary>
    public int LittleEndianFlag { get; }
    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }
    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }
    /// <summary>True bit depth per pixel per plane (1..16).</summary>
    public int PixelDepthPerPlane { get; }
    /// <summary>Number of frames.</summary>
    public int FrameCount { get; }
    /// <summary>Observer name.</summary>
    public string Observer { get; }
    /// <summary>Instrument / camera name.</summary>
    public string Instrument { get; }
    /// <summary>Telescope name.</summary>
    public string Telescope { get; }
    /// <summary>Recording start time (local), as raw .NET-compatible ticks; 0 or negative means "unset / no trailer".</summary>
    public long DateTimeTicks { get; }
    /// <summary>Recording start time (UTC), as raw .NET-compatible ticks; 0 means "unset".</summary>
    public long DateTimeUtcTicks { get; }

    private SerHeader(string fileId, int luId, SerColorId colorId, int littleEndianFlag, int width, int height,
        int pixelDepthPerPlane, int frameCount, string observer, string instrument, string telescope,
        long dateTimeTicks, long dateTimeUtcTicks)
    {
        FileId = fileId;
        LuId = luId;
        ColorId = colorId;
        LittleEndianFlag = littleEndianFlag;
        Width = width;
        Height = height;
        PixelDepthPerPlane = pixelDepthPerPlane;
        FrameCount = frameCount;
        Observer = observer;
        Instrument = instrument;
        Telescope = telescope;
        DateTimeTicks = dateTimeTicks;
        DateTimeUtcTicks = dateTimeUtcTicks;
    }

    /// <summary>
    /// Creates a header for writing. <paramref name="littleEndianFlag"/> defaults to the value that
    /// matches the host byte order under the Match-SER-Player convention (0 on a little-endian host).
    /// </summary>
    public static SerHeader Create(SerColorId colorId, int width, int height, int pixelDepthPerPlane, int frameCount,
        string observer = "", string instrument = "", string telescope = "",
        long dateTimeTicks = 0, long dateTimeUtcTicks = 0, int luId = 0, int littleEndianFlag = -1)
    {
        if (littleEndianFlag < 0)
        {
            // Match-SER-Player: flag 0 == little-endian pixel data, flag 1 == big-endian.
            littleEndianFlag = BitConverter.IsLittleEndian ? 0 : 1;
        }

        return new SerHeader(FileIdValue, luId, colorId, littleEndianFlag, width, height, pixelDepthPerPlane,
            frameCount, observer ?? "", instrument ?? "", telescope ?? "", dateTimeTicks, dateTimeUtcTicks);
    }

    /// <summary>Bytes per sample: 1 for 1..8-bit data, 2 for 9..16-bit data.</summary>
    public int BytesPerSample => PixelDepthPerPlane <= 8 ? 1 : 2;

    /// <summary>Colour planes stored per pixel (3 for RGB/BGR, otherwise 1).</summary>
    public int PlaneCount => ColorId.PlaneCount;

    /// <summary>Size of one frame's pixel data in bytes.</summary>
    public long FrameSizeBytes => (long)Width * Height * PlaneCount * BytesPerSample;

    /// <summary>Maximum representable sample value for the declared bit depth (255 for 1..8-bit; <c>2^depth - 1</c> for 9..16-bit).</summary>
    public int MaxSampleValue => BytesPerSample == 1 ? 255 : (1 << PixelDepthPerPlane) - 1;

    /// <summary>
    /// Whether 16-bit pixel data is little-endian, per the <b>Match-SER-Player</b> convention:
    /// <c>LittleEndianFlag == 0</c> means little-endian, <c>== 1</c> means big-endian (the opposite
    /// of the field's name, but what the reference players actually do).
    /// </summary>
    public bool DataLittleEndian => LittleEndianFlag == 0;

    /// <summary>True when <see cref="FileId"/> matches the canonical signature.</summary>
    public bool HasValidFileId => FileId == FileIdValue;

    /// <summary>Local recording start time, or null when unset / out of range.</summary>
    public DateTime? LocalDateTime => ToDateTime(DateTimeTicks, DateTimeKind.Unspecified);

    /// <summary>UTC recording start time, or null when unset / out of range.</summary>
    public DateTime? UtcDateTime => ToDateTime(DateTimeUtcTicks, DateTimeKind.Utc);

    private static DateTime? ToDateTime(long ticks, DateTimeKind kind)
        => ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, kind) : null;

    /// <summary>Parses a 178-byte header from <paramref name="source"/>.</summary>
    public static SerHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException($"A SER header needs at least {Size} bytes, got {source.Length}.", nameof(source));
        }

        var fileId = Encoding.ASCII.GetString(source[..14]).TrimEnd('\0', ' ');
        var luId = BinaryPrimitives.ReadInt32LittleEndian(source[14..]);
        var colorId = (SerColorId)BinaryPrimitives.ReadInt32LittleEndian(source[18..]);
        var littleEndian = BinaryPrimitives.ReadInt32LittleEndian(source[22..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(source[26..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(source[30..]);
        var depth = BinaryPrimitives.ReadInt32LittleEndian(source[34..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(source[38..]);
        var observer = ReadString(source.Slice(42, StringFieldLength));
        var instrument = ReadString(source.Slice(82, StringFieldLength));
        var telescope = ReadString(source.Slice(122, StringFieldLength));
        var dateTime = BinaryPrimitives.ReadInt64LittleEndian(source[162..]);
        var dateTimeUtc = BinaryPrimitives.ReadInt64LittleEndian(source[170..]);

        return new SerHeader(fileId, luId, colorId, littleEndian, width, height, depth, count,
            observer, instrument, telescope, dateTime, dateTimeUtc);
    }

    /// <summary>Serialises this header into <paramref name="dest"/> (which must be at least <see cref="Size"/> bytes).</summary>
    public void Write(Span<byte> dest)
    {
        if (dest.Length < Size)
        {
            throw new ArgumentException($"A SER header needs at least {Size} bytes, got {dest.Length}.", nameof(dest));
        }

        dest[..Size].Clear();
        WriteString(dest[..14], string.IsNullOrEmpty(FileId) ? FileIdValue : FileId);
        BinaryPrimitives.WriteInt32LittleEndian(dest[14..], LuId);
        BinaryPrimitives.WriteInt32LittleEndian(dest[18..], (int)ColorId);
        BinaryPrimitives.WriteInt32LittleEndian(dest[22..], LittleEndianFlag);
        BinaryPrimitives.WriteInt32LittleEndian(dest[26..], Width);
        BinaryPrimitives.WriteInt32LittleEndian(dest[30..], Height);
        BinaryPrimitives.WriteInt32LittleEndian(dest[34..], PixelDepthPerPlane);
        BinaryPrimitives.WriteInt32LittleEndian(dest[38..], FrameCount);
        WriteString(dest.Slice(42, StringFieldLength), Observer);
        WriteString(dest.Slice(82, StringFieldLength), Instrument);
        WriteString(dest.Slice(122, StringFieldLength), Telescope);
        BinaryPrimitives.WriteInt64LittleEndian(dest[162..], DateTimeTicks);
        BinaryPrimitives.WriteInt64LittleEndian(dest[170..], DateTimeUtcTicks);
    }

    private static string ReadString(ReadOnlySpan<byte> field)
    {
        var nul = field.IndexOf((byte)0);
        if (nul >= 0)
        {
            field = field[..nul];
        }

        return Encoding.ASCII.GetString(field).TrimEnd();
    }

    private static void WriteString(Span<byte> dest, string value)
    {
        // ASCII (printable 32..126), zero-padded, truncated to the field length. The header buffer
        // is cleared before this is called, so the tail is already zero.
        var count = Math.Min(value.Length, dest.Length);
        for (var i = 0; i < count; i++)
        {
            var ch = value[i];
            dest[i] = ch is >= (char)32 and <= (char)126 ? (byte)ch : (byte)'?';
        }
    }
}
