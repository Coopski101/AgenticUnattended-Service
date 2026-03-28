namespace AgenticUnattended.Hooks;

public interface ITranscriptFileReader
{
    bool Exists(string path);
    long GetLength(string path);
    Stream OpenRead(string path);
}

public sealed class TranscriptFileReader : ITranscriptFileReader
{
    public bool Exists(string path) => File.Exists(path);

    public long GetLength(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
}
