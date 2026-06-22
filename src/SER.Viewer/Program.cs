// SER.Viewer - standalone SER planetary-video player (SDL3 + Vulkan via SdlVulkan.Renderer).
//
// Commands:
//   ser-viewer <file.ser>                          open a SER file in the interactive viewer (GUI, in progress)
//   ser-viewer render <in.ser> <frame> <out.png>   decode one frame to a PNG (headless)
//   ser-viewer info <file.ser>                      print header / metadata
//
// The interactive GUI lands in the next phase; the headless paths above already exercise the full
// decode + debayer + auto-stretch pipeline.

using SharpAstro.Ser;
using SharpAstro.Ser.Viewer;

return args switch
{
    [] => Usage(),
    ["render", .. var rest] => RenderCommand.Run(rest),
    ["info", var infoPath, ..] => Info(infoPath),
    [var path, ..] => Info(path),
};

static int Usage()
{
    Console.WriteLine("ser-viewer <file.ser>                          open a SER file (interactive viewer, in progress)");
    Console.WriteLine("ser-viewer render <in.ser> <frame> <out.png>   render one frame to PNG (headless)");
    Console.WriteLine("ser-viewer info <file.ser>                     print header / metadata");
    return 0;
}

static int Info(string path)
{
    using var reader = SerReader.Open(path);
    var h = reader.Header;
    Console.WriteLine($"{path}");
    Console.WriteLine($"  {reader.Width}x{reader.Height}  {reader.ColorId}  {reader.PixelDepthPerPlane}-bit  {reader.FrameCount} frames");
    Console.WriteLine($"  observer='{h.Observer}'  instrument='{h.Instrument}'  telescope='{h.Telescope}'");
    Console.WriteLine($"  timestamps={reader.HasTimestamps}  fps={reader.FramesPerSecond:F1}  utcStart={h.UtcDateTime:u}");
    Console.WriteLine("  (Interactive viewer GUI coming in the next phase.)");
    return 0;
}
