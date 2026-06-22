# SER.Lib

A pure-managed **reader and writer for the SER planetary-imaging video format** (the *Lucam
Recorder* format, spec v3), plus a standalone cross-platform **viewer** (`ser-viewer`).

SER is the de-facto container for lunar / planetary / solar *lucky-imaging* captures: thousands of
raw mono, Bayer, or RGB frames in one file, with optional per-frame UTC timestamps. SER.Lib decodes
and encodes it with **memory-mapped, frame-accurate random access** (seek to any frame in O(1),
even in multi-gigabyte files), and is **AOT- and trim-friendly** with zero external dependencies.

> Part of the [SharpAstro](https://github.com/SharpAstro) family (alongside `FITS.Lib`, `DIR.Lib`,
> `SdlVulkan.Renderer`).

## Install

```
dotnet add package SER.Lib
```

## Quick start

```csharp
using SharpAstro.Ser;

using var ser = SerReader.Open("jupiter.ser");

Console.WriteLine($"{ser.Width}x{ser.Height}  {ser.ColorId}  " +
                  $"{ser.PixelDepthPerPlane}-bit  {ser.FrameCount} frames");

// Random access: decode frame 1000 into a reusable buffer (no per-frame allocation).
var frame = new ushort[ser.Width * ser.Height * ser.ColorId.PlaneCount];
ser.ReadFrame16(index: 1000, frame);

// Per-frame UTC timestamps (when the file carries the optional trailer).
if (ser.HasTimestamps)
    Console.WriteLine(ser.Timestamps[1000].ToString("u"));
```

Writing:

```csharp
using var w = new SerWriter("out.ser", width: 640, height: 480,
    colorId: SerColorId.BayerRGGB, pixelDepthPerPlane: 8);

foreach (var (bytes, time) in frames)
    w.AppendFrame(bytes, time);   // timestamps -> the v3 trailer on Close()
```

## Format notes & gotchas

The on-disk format is a fixed **178-byte little-endian header**, the frame data, and an optional
per-frame timestamp **trailer**. A few real-world subtleties SER.Lib handles for you:

- **The `LittleEndian` header flag is interpreted the way SER Player / PIPP do** (the de-facto
  reference): for 16-bit data, `LittleEndian == 0` means little-endian samples and `LittleEndian == 1`
  means **big-endian** — the opposite of what the field name suggests. This is a well-known wart in
  the ecosystem; matching the reference players is what makes real captures display correctly. The
  header's own integers are always little-endian regardless of this flag.
- **Timestamps are .NET `DateTime` ticks** (100 ns since 0001-01-01). SER.Lib surfaces them as
  UTC `DateTimeOffset`s, applying the same UTC-vs-local detection the reference player uses.
- **Frames are stored top-row-first** (the first pixel is the upper-left); SER.Lib does not flip rows.
- **v2 vs v3:** RGB/BGR true colour, arbitrary bit depths (1-16 with MSB/LSB alignment), and the
  timestamp trailer + UTC start time are v3 additions. SER.Lib reads v2 files (mono/Bayer, 8/16-bit,
  no trailer) transparently.

## `ser-viewer`

A standalone SDL3 + Vulkan viewer with frame playback, a scrub transport, and inline
processing/histogram panels — built on `SdlVulkan.Renderer` + `DIR.Lib`. Published as native
AOT binaries (including **win-arm64**). See the repository releases.

## License

MIT - see [LICENSE](LICENSE).
