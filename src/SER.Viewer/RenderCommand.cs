using SharpAstro.Ser;

namespace SharpAstro.Ser.Viewer;

/// <summary>
/// Headless <c>render</c> command: decode one frame and write it as a PNG. Lets the image pipeline be
/// validated (debayer, orientation, stretch) without an interactive window, and is the basis for the
/// later frame-export feature.
/// </summary>
public static class RenderCommand
{
    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: ser-viewer render <input.ser> <frameIndex> <output.png>");
            return 2;
        }

        var input = args[0];
        if (!int.TryParse(args[1], out var frame))
        {
            Console.Error.WriteLine($"frameIndex must be an integer, got '{args[1]}'.");
            return 2;
        }

        var output = args[2];

        using var reader = SerReader.Open(input);
        if ((uint)frame >= (uint)reader.FrameCount)
        {
            Console.Error.WriteLine($"frame {frame} is out of range [0, {reader.FrameCount}).");
            return 2;
        }

        var rgba = SerImaging.RenderFrameToRgba(reader, frame);
        var png = SharpAstro.Png.PngWriter.Encode(rgba, reader.Width, reader.Height);
        File.WriteAllBytes(output, png);

        Console.WriteLine($"Wrote {output}  ({reader.Width}x{reader.Height}, {reader.ColorId}, frame {frame}/{reader.FrameCount}).");
        return 0;
    }
}
