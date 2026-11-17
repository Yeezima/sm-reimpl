using System.Security.Cryptography;
using SmContent.Extras.Warfront.Maps;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Maps;

namespace SmNet.Tests.Extras.Warfront.Maps;

/// <summary>Warfront slice W5d: the server's, the prebuild's and the client's layout cache files are the same bytes under different process kinds, so one prebuild warms both and the client reads any kind's file.</summary>
public class WarfrontSharedCacheTests
{
    [Fact]
    [Trait("Speed", "Slow")]
    public void Two_process_kinds_write_identical_bytes_and_either_reads_the_others_file()
    {
        var slot = WarfrontSlots.All[0];
        var build = WarfrontSlots.Build(slot);
        string dir = Path.Combine(Path.GetTempPath(), "warfront-shared-" + Guid.NewGuid().ToString("N"));
        try
        {
            Func<WarfrontSlot, (AshfallLevel, AshfallDressing)> gen = _ => (build.Level, build.Dressing);
            WarfrontLayoutCache.LoadOrGenerate(slot, dir, "dotnet", gen, out _);
            WarfrontLayoutCache.LoadOrGenerate(slot, dir, "godotv472stablemonowin64", gen, out _);
            string a = WarfrontLayoutCache.PathIn(dir, slot, "dotnet"), b = WarfrontLayoutCache.PathIn(dir, slot, "godotv472stablemonowin64");
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a))), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(b))));   // one shared key: the layout bytes are identical

            // a client of a third kind reads either file and gets the same level
            var read = WarfrontSharedCache.TryRead(dir, slot, "client", out string note);
            Assert.NotNull(read);
            Assert.Equal(build.Level.Hash, read!.Value.Level.Hash);
            Assert.Equal(WarfrontWorld.Compose(build.Level).Hash, WarfrontWorld.Compose(read.Value.Level).Hash);
            Assert.Contains("read from", note);

            // damaged: the first file is refused, the second still serves; both damaged: null (the caller generates)
            var bytes = File.ReadAllBytes(a); bytes[bytes.Length / 2] ^= 0xFF; File.WriteAllBytes(a, bytes);
            Assert.NotNull(WarfrontSharedCache.TryRead(dir, slot, "dotnet", out _));
            File.WriteAllBytes(b, bytes);
            Assert.Null(WarfrontSharedCache.TryRead(dir, slot, "dotnet", out _));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void No_folder_or_no_file_gives_null_without_throwing()
    {
        var slot = WarfrontSlots.All[0];
        Assert.Null(WarfrontSharedCache.TryRead(Path.Combine(Path.GetTempPath(), "warfront-missing-" + Guid.NewGuid().ToString("N")), slot, "x", out string note));
        Assert.Contains("no readable", note);
    }
}
