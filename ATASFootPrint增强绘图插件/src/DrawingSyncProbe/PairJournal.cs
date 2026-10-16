using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WolfMoss.ATAS.DrawingSync;

public sealed class PairJournal : IDisposable
{
    private sealed record Document(int Version, string Scope, Binding[] Bindings);
    private readonly string _scope;
    private readonly string _path;
    private readonly FileStream _lease;
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WolfMoss", "DrawingSyncProbe");

    public PairJournal(string scope, string? directory = null)
    {
        _scope = scope;
        directory ??= Root;
        Directory.CreateDirectory(directory);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope)));
        _path = Path.Combine(directory, name + ".json");
        // Prevent another ATAS process from concurrently using the same mapping.
        _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public IReadOnlyList<Binding> Load()
    {
        if (!File.Exists(_path)) return [];
        var doc = JsonSerializer.Deserialize<Document>(File.ReadAllText(_path))
            ?? throw new InvalidDataException("配对文件为空。");
        if (doc.Version != 1 || doc.Scope != _scope || doc.Bindings is null || doc.Bindings.Length > SyncEngine.MaxLivePairs + SyncEngine.UndoHistoryLimit)
            throw new InvalidDataException("配对文件版本或作用域不匹配。");
        return doc.Bindings;
    }

    public void Save(IReadOnlyList<Binding> bindings)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(1, _scope, bindings.ToArray()));
        var temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, _path, overwrite: true);
    }

    public void Dispose() => _lease.Dispose();
}
