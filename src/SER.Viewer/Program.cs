// SER.Viewer - standalone SER planetary-video player (SDL3 + Vulkan via SdlVulkan.Renderer).
//
// The interactive GUI (frame playback, scrub transport, inline processing/histogram panels)
// lands in a later phase. This entry point is intentionally minimal so the project is wired
// into the solution + CI from the start; it currently just reports its arguments.

if (args.Length == 0)
{
    Console.WriteLine("ser-viewer <file.ser>");
    Console.WriteLine("Standalone SER planetary-video viewer (GUI in progress).");
    return 0;
}

Console.WriteLine($"ser-viewer: requested file '{args[0]}' (viewer GUI not yet implemented).");
return 0;
