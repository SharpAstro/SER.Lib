namespace SharpAstro.Ser.Tests;

/// <summary>A scratch file path under the OS temp directory, deleted (best-effort) on dispose.</summary>
internal sealed class TempFile : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "serlib-test-" + Guid.NewGuid().ToString("N") + ".ser");

    public void Dispose()
    {
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; the OS will reclaim the temp file regardless.
        }
    }
}
