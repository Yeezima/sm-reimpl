using SmServer;

namespace SmNet.Tests.Extras.Warfront.Stage;

/// <summary>Warfront perf pass: the autosave's off-thread writer keeps the order of the saves, replaces a waiting text with the latest, and has everything on disk after <see cref="WarfrontAsyncWriter.Flush"/>.</summary>
public class WarfrontAsyncWriterTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "sm-wf-async-" + Guid.NewGuid().ToString("N"));
    readonly List<string> _log = new();

    public WarfrontAsyncWriterTests() { Directory.CreateDirectory(_dir); }
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void FlushLeavesTheLatestTextOnDiskAndNoTempFile()
    {
        var w = new WarfrontAsyncWriter(_log.Add);
        string path = Path.Combine(_dir, "run.json");
        for (int i = 0; i < 200; i++) w.Submit(path, "version " + i);
        Assert.True(w.Flush());
        Assert.Equal("version 199", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Empty(_log);
        Assert.True(w.Written >= 1 && w.Written <= 200);
    }

    [Fact]
    public void TwoFilesBothLand()
    {
        var w = new WarfrontAsyncWriter(_log.Add);
        string a = Path.Combine(_dir, "squads.json"), b = Path.Combine(_dir, "run.json");
        w.Submit(a, "squads"); w.Submit(b, "run");
        Assert.True(w.Flush());
        Assert.Equal("squads", File.ReadAllText(a));
        Assert.Equal("run", File.ReadAllText(b));
    }

    [Fact]
    public void AFailedWriteIsLoggedAndTheWriterKeepsWorking()
    {
        var w = new WarfrontAsyncWriter(_log.Add);
        w.Submit(Path.Combine(_dir, "missing", "run.json"), "x");   // the folder does not exist
        Assert.True(w.Flush());
        Assert.Single(_log);
        string ok = Path.Combine(_dir, "run.json");
        w.Submit(ok, "fine");
        Assert.True(w.Flush());
        Assert.Equal("fine", File.ReadAllText(ok));
    }

    [Fact]
    public void FlushOnAnIdleWriterReturnsAtOnce()
    {
        var w = new WarfrontAsyncWriter(_log.Add);
        Assert.True(w.Flush(50));
    }
}
