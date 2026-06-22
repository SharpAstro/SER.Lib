// SER.Viewer - standalone SER planetary-video player (SDL3 + Vulkan via SdlVulkan.Renderer).
//
// Commands:
//   ser-viewer <file.ser>                          open in the interactive viewer (playback + scrub)
//   ser-viewer render <in.ser> <frame> <out.png>   decode one frame to a PNG (headless)
//   ser-viewer info <file.ser>                      print header / metadata

using DIR.Lib;
using SdlVulkan.Renderer;
using SharpAstro.Ser;
using SharpAstro.Ser.Viewer;

return args switch
{
    [] => Usage(),
    ["render", .. var rest] => RenderCommand.Run(rest),
    ["info", var infoPath, ..] => Info(infoPath),
    [var path, ..] => RunViewer(path),
};

static int Usage()
{
    Console.WriteLine("ser-viewer <file.ser>                          open in the interactive viewer");
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
    return 0;
}

static int RunViewer(string path)
{
    using var reader = SerReader.Open(path);

    using var window = SdlVulkanWindow.Create($"ser-viewer - {Path.GetFileName(path)}", 1100, 800);
    window.GetSizeInPixels(out var pixelW, out var pixelH);
    var ctx = VulkanContext.Create(window.Instance, window.Surface, (uint)pixelW, (uint)pixelH);
    var renderer = new VkRenderer(ctx, (uint)pixelW, (uint)pixelH);
    var viewer = new VkSerViewer(reader, ctx, renderer);
    using var cts = new CancellationTokenSource();

    renderer.OnPreRenderPass = cmd => viewer.RecordUploads(cmd);

    var loop = new SdlEventLoop(window, renderer)
    {
        BackgroundColor = new RGBAColor32(0x0a, 0x0a, 0x0a, 0xff),
        CheckNeedsRedraw = viewer.PrepareFrame,
        OnRender = viewer.Render,
        OnPostFrame = viewer.OnPostFrame,
        OnKeyDown = (key, _) =>
        {
            switch (key)
            {
                case InputKey.Escape:
                    cts.Cancel();
                    return true;
                case InputKey.F11:
                    window.ToggleFullscreen();
                    return true;
                default:
                    return viewer.OnKey(key);
            }
        },
    };

    try
    {
        loop.Run(cts.Token);
    }
    finally
    {
        // Teardown order matters: the viewer's GPU textures (samplers/images/descriptor sets) must be
        // freed while the device is still alive, and only once the GPU is idle. Disposing the context
        // (which destroys the device) before the textures is what crashed an earlier build on Esc.
        ctx.GraphicsDevice.WaitIdle();
        viewer.Dispose();
        renderer.Dispose();
        ctx.Dispose();
    }

    return 0;
}
