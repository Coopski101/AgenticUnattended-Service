using AgenticUnattended.Hooks;

namespace AgenticUnattended.Tests.Fakes;

public sealed class FakeTranscriptFileReader : ITranscriptFileReader
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public void SetFileContent(string path, string content)
    {
        _files[path] = System.Text.Encoding.UTF8.GetBytes(content);
    }

    public void AppendContent(string path, string content)
    {
        var existing = _files.TryGetValue(path, out var bytes) ? bytes : [];
        var append = System.Text.Encoding.UTF8.GetBytes(content);
        var combined = new byte[existing.Length + append.Length];
        existing.CopyTo(combined, 0);
        append.CopyTo(combined, existing.Length);
        _files[path] = combined;
    }

    public void RemoveFile(string path) => _files.Remove(path);

    public bool Exists(string path) => _files.ContainsKey(path);

    public long GetLength(string path) =>
        _files.TryGetValue(path, out var bytes) ? bytes.Length : 0;

    public Stream OpenRead(string path)
    {
        if (!_files.TryGetValue(path, out var bytes))
            throw new FileNotFoundException("Fake file not found", path);
        return new MemoryStream(bytes, writable: false);
    }
}
