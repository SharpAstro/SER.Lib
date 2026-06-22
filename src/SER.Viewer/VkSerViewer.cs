using System.Diagnostics;
using DIR.Lib;
using SdlVulkan.Renderer;
using Vortice.Vulkan;

namespace SharpAstro.Ser.Viewer;

/// <summary>
/// Interactive viewer state + per-frame rendering for one open SER file. Decodes the current frame on
/// the CPU (debayer + display map), uploads it as a GPU texture, and draws it letterboxed with a small
/// status overlay. Playback advances by wall-clock at a selectable rate.
/// </summary>
/// <remarks>
/// Frame flow matches SdlVulkan.Renderer's loop: <see cref="PrepareFrame"/> runs in the loop's
/// CheckNeedsRedraw (before BeginFrame) and builds the next texture; <see cref="RecordUploads"/> runs
/// in the renderer's OnPreRenderPass; <see cref="Render"/> runs in OnRender. A small deferred-dispose
/// queue retires old textures a few frames after they were last drawn (GPU still in flight).
/// </remarks>
public sealed class VkSerViewer(SerReader reader, VulkanContext ctx, VkRenderer renderer) : IDisposable
{
    private static readonly float[] PlaybackRates = [1, 5, 10, 15, 24, 30, 50, 75, 100, 150, 200];

    private readonly int _width = reader.Width;
    private readonly int _height = reader.Height;
    private readonly string _font = ResolveFont();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(VkTexture Texture, int Age)> _retired = [];

    private int _frameIndex;
    private int _shownFrame = -1;
    private bool _playing;
    private int _rateIndex = 5; // 30 fps
    private double _playAnchorSeconds;
    private int _playAnchorFrame;
    private VkTexture? _current;

    private float PlaybackFps => PlaybackRates[_rateIndex];

    /// <summary>Runs before BeginFrame. Advances playback, (re)builds the current texture, and reports whether a redraw is needed.</summary>
    public bool PrepareFrame()
    {
        var desired = _frameIndex;
        if (_playing && reader.FrameCount > 1)
        {
            var elapsed = _clock.Elapsed.TotalSeconds - _playAnchorSeconds;
            desired = _playAnchorFrame + (int)(elapsed * PlaybackFps);
            desired %= reader.FrameCount; // loop the sequence
            if (desired < 0) desired += reader.FrameCount;
            _frameIndex = desired;
        }

        if (desired == _shownFrame && _current is not null)
        {
            return false; // nothing new to draw (loop will idle until the clock advances or input arrives)
        }

        var rgba = SerImaging.RenderFrameToRgba(reader, desired);
        var texture = VkTexture.CreateDeferred(ctx, rgba, _width, _height, VkFormat.R8G8B8A8Unorm);
        if (_current is not null)
        {
            _retired.Add((_current, 0));
        }

        _current = texture;
        _shownFrame = desired;
        return true;
    }

    /// <summary>Runs in OnPreRenderPass: record the pending texture upload (transfers must precede the render pass).</summary>
    public void RecordUploads(VkCommandBuffer cmd)
    {
        if (_current is { IsUploaded: false } texture)
        {
            texture.RecordUpload(cmd);
        }
    }

    /// <summary>Runs in OnRender: draw the current frame letterboxed + the status overlay.</summary>
    public void Render()
    {
        if (_current is null)
        {
            return;
        }

        float winW = renderer.Width;
        float winH = renderer.Height;
        var scale = Math.Min(winW / _width, winH / _height);
        var drawW = _width * scale;
        var drawH = _height * scale;
        var x = (winW - drawW) * 0.5f;
        var y = (winH - drawH) * 0.5f;
        renderer.DrawTexture(_current.DescriptorSet, x, y, drawW, drawH);

        var time = reader.HasTimestamps ? reader.Timestamps[_shownFrame].ToString("HH:mm:ss.fff") + " UT" : "no timestamps";
        var label =
            $"{(_playing ? "PLAY " : "PAUSE")}  frame {_shownFrame + 1}/{reader.FrameCount}   " +
            $"{_width}x{_height} {reader.ColorId} {reader.PixelDepthPerPlane}-bit   {time}   {PlaybackFps:F0} fps";
        var help = "Space: play/pause   Left/Right: step   Up/Down: speed   Home/End: first/last   F11: fullscreen   Esc: quit";

        DrawLine(label, 10, 8);
        DrawLine(help, 10, (int)winH - 28);
    }

    private void DrawLine(string text, int left, int top)
    {
        var width = (int)renderer.Width - (left * 2);
        var rect = new RectInt((left + Math.Max(width, 1), top + 22), (left, top));
        renderer.DrawText(text, _font, 16f, new RGBAColor32(0xff, 0xff, 0xff, 0xff), rect, TextAlign.Near, TextAlign.Near);
    }

    /// <summary>Runs in OnPostFrame: free staging memory and dispose textures that are safely out of flight.</summary>
    public void OnPostFrame()
    {
        if (_current is { IsUploaded: true } texture)
        {
            texture.CleanupStaging();
        }

        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            var (tex, age) = _retired[i];
            if (age >= 3)
            {
                tex.Dispose();
                _retired.RemoveAt(i);
            }
            else
            {
                _retired[i] = (tex, age + 1);
            }
        }
    }

    /// <summary>Handles a key press; returns true if it was consumed.</summary>
    public bool OnKey(InputKey key)
    {
        switch (key)
        {
            case InputKey.Space:
                _playing = !_playing;
                if (_playing) ReanchorPlayback();
                return true;
            case InputKey.Right:
                Step(+1);
                return true;
            case InputKey.Left:
                Step(-1);
                return true;
            case InputKey.Home:
                _playing = false;
                _frameIndex = 0;
                return true;
            case InputKey.End:
                _playing = false;
                _frameIndex = reader.FrameCount - 1;
                return true;
            case InputKey.Up:
                _rateIndex = Math.Min(_rateIndex + 1, PlaybackRates.Length - 1);
                ReanchorPlayback();
                return true;
            case InputKey.Down:
                _rateIndex = Math.Max(_rateIndex - 1, 0);
                ReanchorPlayback();
                return true;
            default:
                return false;
        }
    }

    private void Step(int delta)
    {
        _playing = false;
        _frameIndex = Math.Clamp(_frameIndex + delta, 0, reader.FrameCount - 1);
    }

    private void ReanchorPlayback()
    {
        _playAnchorSeconds = _clock.Elapsed.TotalSeconds;
        _playAnchorFrame = _frameIndex;
    }

    private static string ResolveFont()
    {
        var font = FontResolver.ResolveSystemFont();
        return string.IsNullOrEmpty(font) ? @"C:\Windows\Fonts\segoeui.ttf" : font;
    }

    public void Dispose()
    {
        _current?.Dispose();
        foreach (var (tex, _) in _retired)
        {
            tex.Dispose();
        }

        _retired.Clear();
    }
}
