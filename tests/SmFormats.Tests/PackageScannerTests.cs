using System.IO.Compression;
using System.Text;
using SmTool;

namespace SmFormats.Tests;

/// <summary>The release content guard (smtool scan-package). Every fixture is synthetic: no original bytes.</summary>
public sealed class PackageScannerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "sm-scan-" + Guid.NewGuid().ToString("N"));

    public PackageScannerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    string Zip(params (string Name, byte[] Data)[] entries)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        return path;
    }

    /// <summary>The fixtures are assembled at run time (%U% = Users, %H% = home, %AT% = @), so this file does not trip the guard when it ships in the source zip.</summary>
    static string Fix(string s) => s.Replace("%U%", "Users").Replace("%H%", "home").Replace("%AT%", "@");

    static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);
    static byte[] Filler(int n, byte value = 0x41) => Enumerable.Repeat(value, n).ToArray();

    static List<ScanFinding> Scan(string zip, Action<PackageScanner>? setup = null)
    {
        var scanner = new PackageScanner();
        setup?.Invoke(scanner);
        return scanner.ScanPackage(zip);
    }

    [Fact]
    public void CleanPackagePasses()
    {
        string zip = Zip(("README-FIRST.txt", Text("You need your own copy of the game.")), ("SmServer.dll", Filler(10_000)), ("LICENSE", Text("GPL")));
        Assert.Empty(Scan(zip));
    }

    [Theory]
    [InlineData("data/packages/ebps.oppc")]
    [InlineData("sound/weapon.BNK")]
    [InlineData("a/b/voice.wem")]
    [InlineData("map.hkt")]
    [InlineData("rig.hkx")]
    [InlineData("mesh.dcm")]
    [InlineData("mat.bmat")]
    [InlineData("tex.dds")]
    [InlineData("shader.fx")]
    [InlineData("tune.baf")]
    [InlineData("object.O3d")]
    [InlineData("lights.Layer")]
    [InlineData("shape.ppk")]
    [InlineData("blob.bin")]
    public void GameContentExtensionsAreRefused(string name)
    {
        var findings = Scan(Zip((name, Text("harmless synthetic bytes"))));
        Assert.Contains(findings, f => f.Rule == "forbidden-extension");
    }

    [Theory]
    [InlineData("OBPK")]
    [InlineData("AKPK")]
    [InlineData("BKHD")]
    [InlineData("DDS ")]
    public void KnownGameMagicIsRefusedWhateverTheNameOrSize(string magic)
    {
        byte[] data = Text(magic).Concat(Filler(200_000)).ToArray();
        var findings = Scan(Zip(("innocent/readme.txt", data)));
        Assert.Contains(findings, f => f.Rule == "game-format-magic");
    }

    [Fact]
    public void HavokPackfileMagicIsRefused()
    {
        byte[] data = new byte[] { 0x57, 0xE0, 0xE0, 0x57, 0x10, 0xC0, 0xC0, 0x10 }.Concat(Filler(64)).ToArray();
        Assert.Contains(Scan(Zip(("x/y.data", data))), f => f.Rule == "game-format-magic");
    }

    [Fact]
    public void GodotsOwnEmbeddedPackIsAllowedButAnyOtherPckIsNot()
    {
        byte[] standalone = GodotExe(4, 2, false, false, "project.binary", "Main.tscn.remap").Skip(500).ToArray();      // a pack file proper starts at byte 0
        Assert.Empty(Scan(Zip(("sm-reimpl.pck", standalone))));
        Assert.Contains(Scan(Zip(("sm-reimpl.pck", Text("GDPC").Concat(Filler(100)).ToArray()))), f => f.Rule == "pack-unverifiable");     // the magic alone proves nothing
        Assert.Contains(Scan(Zip(("weapon_bolter.pck", Text("AKPK").Concat(Filler(100)).ToArray()))), f => f.Rule == "forbidden-extension");
    }

    [Theory]
    [InlineData("cache/audio/ogg/x.txt")]
    [InlineData("sm-reimpl/Cache/x.txt")]
    [InlineData("discovery/2026/report.txt")]
    [InlineData("client/.godot/imported/a.txt")]
    public void PrivateFoldersAreRefused(string name)
    {
        Assert.Contains(Scan(Zip((name, Text("x")))), f => f.Rule == "forbidden-folder");
    }

    [Theory]
    [InlineData("config/local.json")]
    [InlineData("local.json")]
    [InlineData(".env")]
    [InlineData("x/.env.production")]
    public void SecretsConfigFilesAreRefusedAndNeverPrinted(string name)
    {
        string secret = "SECRET-VALUE-" + Guid.NewGuid().ToString("N");
        var findings = Scan(Zip((name, Text("{\"discord\":{\"botToken\":\"" + secret + "\"}}"))));
        Assert.Contains(findings, f => f.Rule == "forbidden-file");
        Assert.DoesNotContain(findings, f => f.ToString().Contains(secret, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"gameDir = C:\%U%\jdoe\Documents\game")]
    [InlineData("path: c:/%U%/jdoe/AppData/Local/sm-reimpl")]
    public void AbsoluteUserPathsAreRefusedAndTheNameIsNotPrinted(string content)
    {
        var findings = Scan(Zip(("notes.txt", Text(Fix(content)))));
        Assert.Contains(findings, f => f.Rule == "absolute-user-path");
        Assert.DoesNotContain(findings, f => f.ToString().Contains("jdoe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Utf16UserPathsInBinariesAreFound()
    {
        byte[] data = Filler(500).Concat(Encoding.Unicode.GetBytes(Fix(@"C:\%U%\jdoe\src\app.pdb"))).Concat(Filler(500)).ToArray();
        Assert.Contains(Scan(Zip(("lib.dll", data))), f => f.Rule == "absolute-user-path");
    }

    [Fact]
    public void PlaceholderUserNamesAndHomePathsInBinariesAreNotFlagged()
    {
        Assert.Empty(Scan(Zip(("a.txt", Text(Fix(@"C:\%U%\<name>\x and C:\%U%\Public\x and %LOCALAPPDATA%\sm-reimpl"))))));
        Assert.Empty(Scan(Zip(("libx.so", Text(Fix("/%H%/builder/src/x.c"))))));
        Assert.Contains(Scan(Zip(("run.sh", Text(Fix("cd /%H%/jdoe/sm-reimpl"))))), f => f.Rule == "absolute-user-path");
    }

    [Fact]
    public void DiscordWebhooksAndBotTokensAreRefused()
    {
        string hook = "https://discord.com/api/webhooks/123456789012345678/" + new string('a', 20) + new string('B', 20) + "-_";
        var findings = Scan(Zip(("cfg.txt", Text("url=" + hook))));
        Assert.Contains(findings, f => f.Rule == "discord-webhook");
        Assert.DoesNotContain(findings, f => f.ToString().Contains("123456789012345678", StringComparison.Ordinal));

        string token = "M" + new string('x', 23) + "." + "abcdef" + "." + new string('y', 30);
        Assert.Contains(Scan(Zip(("cfg.txt", Text("token=" + token)))), f => f.Rule == "discord-bot-token");
    }

    [Fact]
    public void PersonalEmailAddressesAreRefusedButNoreplyAddressesAreNot()
    {
        var findings = Scan(Zip(("AUTHORS.txt", Text(Fix("Jane <jane.doe%AT%gmail.com>")))));
        Assert.Contains(findings, f => f.Rule == "personal-email");
        Assert.DoesNotContain(findings, f => f.ToString().Contains("jane", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Scan(Zip(("AUTHORS.txt", Text("1234+Yeezima@users.noreply.github.com and noreply@anthropic.com and a@example.com")))));
    }

    [Fact]
    public void ShortTestFixtureWebhooksAreNotFlagged()
    {
        Assert.Empty(Scan(Zip(("tests.cs", Text("\"https://discord.com/api/webhooks/123/abc_DEF-9\" and BOTtoken.secret.value-000")))));
    }

    [Fact]
    public void ExactValuesFromTheLocalConfigAreRefusedWithoutBeingPrinted()
    {
        string config = Path.Combine(_root, "local.json");
        string hook = "https://example.invalid/hook/" + Guid.NewGuid().ToString("N");
        File.WriteAllText(config, "{\"gameDir\":\"D:/Games/Space Marine\",\"discord\":{\"webhookUrl\":\"" + hook + "\",\"webhooks\":{\"a\":\"ignored-short\"},\"botToken\":\"\"}}");
        string zip = Zip(("note.txt", Text("see " + hook)), ("dir.txt", Text(@"installed in D:\Games\Space Marine\data")), ("ok.txt", Text("nothing")));
        var findings = Scan(zip, s => Assert.True(s.AddSecretsFromConfig(config) >= 2));
        Assert.Contains(findings, f => f.Entry == "note.txt" && f.Rule == "local-secret-value");
        Assert.Contains(findings, f => f.Entry == "dir.txt" && f.Rule == "local-secret-value");
        Assert.DoesNotContain(findings, f => f.Entry == "ok.txt");
        Assert.DoesNotContain(findings, f => f.ToString().Contains(hook, StringComparison.Ordinal));
    }

    [Fact]
    public void FilesIdenticalToPrivateFilesAreRefusedEvenWhenRenamed()
    {
        string privateDir = Path.Combine(_root, "install");
        Directory.CreateDirectory(privateDir);
        byte[] content = Enumerable.Range(0, 20_000).Select(i => (byte)(i * 31 % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(privateDir, "thing.dat"), content);
        File.WriteAllBytes(Path.Combine(privateDir, "other.dat"), Filler(20_000));

        string zip = Zip(("renamed.txt", content), ("same-size-other.txt", Filler(19_999)), ("innocent.txt", Text("hi")));
        var findings = Scan(zip, s => Assert.Equal(2, s.AddForbiddenTree(privateDir)));
        Assert.Contains(findings, f => f.Entry == "renamed.txt" && f.Rule == "copy-of-private-file");
        Assert.DoesNotContain(findings, f => f.Entry != "renamed.txt");
    }

    [Fact]
    public void SourceTextCopiesInThePrivateCacheAreNotComparedWhenAskedAndLicenceTextsMayNameAuthors()
    {
        string cache = Path.Combine(_root, "cachecopy");
        Directory.CreateDirectory(cache);
        byte[] source = Text("// scratch copy of our own source\n" + new string('x', 10_000));
        File.WriteAllBytes(Path.Combine(cache, "Old.cs"), source);
        Assert.Equal(0, new PackageScanner().AddForbiddenTree(cache, skipSourceText: true));
        Assert.Empty(Scan(Zip(("src/Old.cs", source)), s => s.AddForbiddenTree(cache, skipSourceText: true)));
        Assert.Empty(Scan(Zip(("licenses/dotnet/THIRD-PARTY-NOTICES.TXT", Text(Fix("Author <someone%AT%example.org> and <jane%AT%gmail.com>"))))));
    }

    [Fact]
    public void ASingleLooseFileIsScannedToo()
    {
        string clean = Path.Combine(_root, "notes.md");
        File.WriteAllText(clean, "# fine");
        Assert.Empty(new PackageScanner().ScanPackage(clean));
        string bad = Path.Combine(_root, "bad.md");
        File.WriteAllText(bad, Fix("see C:\\%U%\\jdoe\\x"));
        Assert.Contains(new PackageScanner().ScanPackage(bad), f => f.Rule == "absolute-user-path");
    }

    [Fact]
    public void FoldersAreScannedLikeZips()
    {
        string folder = Path.Combine(_root, "stage");
        Directory.CreateDirectory(Path.Combine(folder, "cache"));
        File.WriteAllText(Path.Combine(folder, "cache", "a.txt"), "x");
        File.WriteAllText(Path.Combine(folder, "ok.txt"), "fine");
        var findings = new PackageScanner().ScanPackage(folder);
        Assert.Single(findings);
        Assert.Equal("cache/a.txt", findings[0].Entry);
    }

    [Fact]
    public void ValuesSplitAcrossReadChunksAreStillFound()
    {
        // A path that starts 5 bytes before the end of the first read must still be seen (the scanner carries an overlap into the next read).
        byte[] pad = Filler(PackageScanner.ReadBufferBytes - 5);
        byte[] data = pad.Concat(Text(Fix(@"C:\%U%\jdoe\x"))).Concat(Filler(100)).ToArray();
        Assert.Contains(Scan(Zip(("big.dll", data))), f => f.Rule == "absolute-user-path");
    }

    [Fact]
    public void ExtensionsOutsideTheAllowlistAreRefusedInTheBinaryProfile()
    {
        void Profile(PackageScanner s) => s.AllowedExtensions = new HashSet<string>(PackageScanCommand.BinaryProfileExtensions, StringComparer.OrdinalIgnoreCase);
        var findings = Scan(Zip(("a/x.dll", Filler(100)), ("a/run.sh", Text("echo")), ("a/LICENSE", Text("x")), ("a/SmServer", Filler(100)), ("a/odd.xyz", Filler(100)), ("a/noext", Filler(100))), Profile);
        Assert.Equal(2, findings.Count(f => f.Rule == "unexpected-type"));
        Assert.Contains(findings, f => f.Entry == "a/odd.xyz");
        Assert.Contains(findings, f => f.Entry == "a/noext");
    }

    /// <summary>A Godot pack of format 4 as the 4.7.2 exporter writes it: header (112 bytes), directory of length-prefixed padded names, then (embedded) size and "GDPC".</summary>
    static byte[] GodotExe(uint version, uint flags, bool trailer, bool corruptDirectory, params string[] names)
    {
        var pack = new MemoryStream();
        var w = new BinaryWriter(pack);
        w.Write("GDPC"u8.ToArray()); w.Write(version); w.Write(4u); w.Write(7u); w.Write(2u); w.Write(flags); w.Write(112UL);
        long dirOffsetAt = pack.Position;
        w.Write(0UL);
        w.Write(new byte[112 - (int)pack.Position]);
        long dir = pack.Position;
        w.Write((uint)names.Length);
        foreach (var name in names)
        {
            byte[] bytes = Text(name);
            int pad = (4 - bytes.Length % 4) % 4;
            w.Write((uint)(bytes.Length + pad)); w.Write(bytes); w.Write(new byte[pad]);
            w.Write(0UL); w.Write(1UL); w.Write(new byte[16]); w.Write(0u);
        }
        byte[] packBytes = pack.ToArray();
        BitConverter.GetBytes((ulong)dir).CopyTo(packBytes, dirOffsetAt);
        if (corruptDirectory) BitConverter.GetBytes(uint.MaxValue).CopyTo(packBytes, dir + 4);
        var exe = new List<byte>(Filler(500));        // a stand-in for the engine's executable
        exe.AddRange(packBytes);
        if (trailer) { exe.AddRange(BitConverter.GetBytes((ulong)packBytes.Length)); exe.AddRange(Text("GDPC")); }
        return exe.ToArray();
    }

    [Fact]
    public void ADecodedGodotPackIsCheckedByItsDirectoryNotByTextSniffing()
    {
        string[] fine = ["project.binary", "Main.tscn.remap", ".godot/uid_cache.bin", ".godot/exported/133200997/export-bcb0d2eb5949c52b6a65bfe9de3e985b-Main.scn", "Some (odd) name.cs", "NetworkView.Fx.cs"];
        Assert.Empty(Scan(Zip(("sm-reimpl.exe", GodotExe(4, 2, true, false, fine)))));
        Assert.Empty(Scan(Zip(("sm-reimpl.exe", GodotExe(3, 2, true, false, fine)))));

        foreach (var bad in new[] { "stray/texture.dds.remap", "weapon.oppc", "screenshots/shot.png", ".godot/imported/tex.dds-0123456789abcdef0123456789abcdef.ctex", "captures/(1) a.png" })
        {
            var findings = Scan(Zip(("sm-reimpl.exe", GodotExe(4, 2, true, false, [.. fine, bad]))));
            Assert.Contains(findings, f => f.Rule == "game-content-in-pack");
        }
        // other executables with a pack are checked too, under any name
        Assert.Contains(Scan(Zip(("renamed.exe", GodotExe(4, 2, true, false, [.. fine, "x/y.wem"])))), f => f.Rule == "game-content-in-pack");
    }

    [Fact]
    public void TheClientExecutableWithoutAVerifiablePackIsRefused()
    {
        string[] fine = ["project.binary", "Main.tscn.remap"];
        void Refused(byte[] exe) => Assert.Contains(Scan(Zip(("sm-reimpl.exe", exe))), f => f.Rule == "pack-unverifiable");
        Refused(Filler(5000));                                                    // no pack at all
        Refused(GodotExe(4, 2, false, false, fine));                              // a pack without the trailer
        Refused(GodotExe(4, 2, true, true, fine));                                // an unparseable directory
        Refused(GodotExe(4, 3, true, false, fine));                               // an encrypted directory
        Refused(GodotExe(9, 2, true, false, fine));                               // a format this guard cannot read
        Refused(GodotExe(4, 2, true, false, ["Main.tscn.remap"]));                // not the client's pack: no project.binary
        Refused(Filler(10).Concat(Text("GDPC")).ToArray());                       // a trailer that does not fit
        // an executable that is not the client's and has no pack is simply not a pack
        Assert.Empty(Scan(Zip(("SmServer.exe", Filler(5000)))));
    }

    [Fact]
    public void AClientPackageWithoutAVerifiedGodotPackIsRefused()
    {
        string[] fine = ["project.binary", "Main.tscn.remap"];
        void Require(PackageScanner s) => s.RequireVerifiedPack = true;
        // a pack that parses and lists project.binary: fine
        Assert.Empty(Scan(Zip(("top/sm-reimpl.exe", GodotExe(4, 2, true, false, fine)), ("top/Play.cmd", Text("x"))), Require));
        // no executable with a pack at all (a console wrapper only, or nothing)
        Assert.Contains(Scan(Zip(("top/sm-reimpl.console.exe", Filler(3000)), ("top/Play.cmd", Text("x"))), Require), f => f.Rule == "no-verified-pack");
        Assert.Contains(Scan(Zip(("top/readme.txt", Text("x"))), Require), f => f.Rule == "no-verified-pack");
        // a pack that does not verify counts as none (and is itself refused)
        var bad = Scan(Zip(("top/sm-reimpl.exe", GodotExe(4, 3, true, false, fine))), Require);
        Assert.Contains(bad, f => f.Rule == "no-verified-pack");
        Assert.Contains(bad, f => f.Rule == "pack-unverifiable");
        // the requirement is per scan: a later package is judged on its own contents
        var scanner = new PackageScanner { RequireVerifiedPack = true };
        Assert.Empty(scanner.ScanPackage(Zip(("top/sm-reimpl.exe", GodotExe(4, 2, true, false, fine)))));
        Assert.Contains(scanner.ScanPackage(Zip(("top/x.txt", Text("x")))), f => f.Rule == "no-verified-pack");
        // not required: servers have no pack
        Assert.Empty(Scan(Zip(("top/SmServer.exe", Filler(3000)))));
        // through the command
        Assert.Equal(0, PackageScanCommand.Run(["--require-pack", Zip(("top/sm-reimpl.exe", GodotExe(4, 2, true, false, fine)))]));
        Assert.Equal(1, PackageScanCommand.Run(["--require-pack", Zip(("top/SmServer.exe", Filler(3000)))]));
    }

    [Fact]
    public void ATarGzIsScannedEntryByEntryAndKeepsTheExecutableMode()
    {
        string folder = Path.Combine(_root, "tarsrc");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "server.sh"), "#!/bin/sh\n");
        File.WriteAllBytes(Path.Combine(folder, "SmServer"), Filler(100));
        File.WriteAllText(Path.Combine(folder, "README.txt"), "hi");
        string tgz = Path.Combine(_root, "pkg.tar.gz");
        TarPackage.Create(folder, tgz, "top", ["server.sh", "SmServer"]);

        using (var gz = new System.IO.Compression.GZipStream(File.OpenRead(tgz), System.IO.Compression.CompressionMode.Decompress))
        using (var tar = new System.Formats.Tar.TarReader(gz))
        {
            var modes = new Dictionary<string, UnixFileMode>();
            while (tar.GetNextEntry() is { } e) modes[e.Name] = e.Mode;
            Assert.Equal(3, modes.Count);
            Assert.True(modes["top/server.sh"].HasFlag(UnixFileMode.UserExecute) && modes["top/server.sh"].HasFlag(UnixFileMode.OtherExecute));
            Assert.True(modes["top/SmServer"].HasFlag(UnixFileMode.GroupExecute));
            Assert.False(modes["top/README.txt"].HasFlag(UnixFileMode.UserExecute));
        }
        Assert.Empty(new PackageScanner().ScanPackage(tgz));

        File.WriteAllText(Path.Combine(folder, "bad.wem"), "x");
        string tgz2 = Path.Combine(_root, "pkg2.tar.gz");
        TarPackage.Create(folder, tgz2, "top", []);
        Assert.Contains(new PackageScanner().ScanPackage(tgz2), f => f.Rule == "forbidden-extension" && f.Entry == "top/bad.wem");
        Assert.Throws<FileNotFoundException>(() => TarPackage.Create(folder, tgz2, "top", ["missing"]));
    }

    [Fact]
    public void AnUnreadablePrivateFileOfTheSameSizeFailsClosed()
    {
        string privateDir = Path.Combine(_root, "locked");
        Directory.CreateDirectory(privateDir);
        string locked = Path.Combine(privateDir, "secret.dat");
        File.WriteAllBytes(locked, Filler(20_000));
        var scanner = new PackageScanner();
        scanner.AddForbiddenTree(privateDir);
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var findings = scanner.ScanPackage(Zip(("same-size.txt", Filler(20_000, 0x42))));
        Assert.Contains(findings, f => f.Rule == "unreadable-private-file");
    }

    [Fact]
    public void SecretsFromArraysAndTheEnvironmentAreLoadedAndABadFileThrows()
    {
        string config = Path.Combine(_root, "settings.json");
        File.WriteAllText(config, "{\"discord\":{\"webhooks\":[\"https://example.invalid/hook-one-aaaa\",{\"x\":\"https://example.invalid/hook-two-bbbb\"}]}}");
        var scanner = new PackageScanner();
        Assert.Equal(2, scanner.AddSecretsFromConfig(config));
        var env = new Dictionary<string, string?> { ["SMREIMPL_DISCORD_WEBHOOK"] = "https://example.invalid/env-hook-cccc", ["OTHER"] = "https://example.invalid/not-a-secret-dd" };
        Assert.Equal(1, scanner.AddSecretsFromEnvironment(k => env.GetValueOrDefault(k), env.Keys));
        var findings = Scan(Zip(("a.txt", Text("https://example.invalid/hook-two-bbbb")), ("b.txt", Text("https://example.invalid/env-hook-cccc")), ("c.txt", Text("https://example.invalid/not-a-secret-dd"))), s => { s.AddSecretsFromConfig(config); s.AddSecretsFromEnvironment(k => env.GetValueOrDefault(k), env.Keys); });
        Assert.Contains(findings, f => f.Entry == "a.txt");
        Assert.Contains(findings, f => f.Entry == "b.txt");
        Assert.DoesNotContain(findings, f => f.Entry == "c.txt");

        string broken = Path.Combine(_root, "broken.json");
        File.WriteAllText(broken, "{ not json");
        Assert.ThrowsAny<Exception>(() => new PackageScanner().AddSecretsFromConfig(broken));
    }

    [Fact]
    public void TheCommandFailsClosedOnInputsItCannotUse()
    {
        string clean = Zip(("a.txt", Text("fine")));
        Directory.CreateDirectory(Path.Combine(_root, "cfg"));
        string empty = Path.Combine(_root, "cfg", "local.json");
        File.WriteAllText(empty, "{}");
        string emptySettings = Path.Combine(_root, "settings.json");
        File.WriteAllText(emptySettings, "{}");
        string broken = Path.Combine(_root, "broken.json");
        File.WriteAllText(broken, "{ not json");
        string missingDir = Path.Combine(_root, "no-such-folder");

        Assert.Equal(0, PackageScanCommand.Run([clean]));
        Assert.Equal(2, PackageScanCommand.Run(["--against", missingDir, clean]));                           // a missing folder to compare against is an error
        Assert.Equal(2, PackageScanCommand.Run(["--secrets", broken, clean]));                               // an unparsable secrets file is an error
        Assert.Equal(2, PackageScanCommand.Run(["--secrets", Path.Combine(_root, "absent.json"), clean]));   // so is a missing one
        Assert.Equal(2, PackageScanCommand.Run(["--secrets", empty, "--require-secrets", clean]));           // required, but no values
        Assert.Equal(0, PackageScanCommand.Run(["--secrets", empty, clean]));                                // not required: fine
        Assert.Equal(2, PackageScanCommand.Run(["--secrets", emptySettings, "--require-secrets", clean]));   // no local.json given at all: nothing can vouch for the secrets
        Assert.Equal(2, PackageScanCommand.Run(["--require-secrets", clean]));
        File.WriteAllText(Path.Combine(_root, "cfg", "local.json"), "{\"discord\":{\"botToken\":\"not-a-real-token-value\"}}");
        Assert.Equal(0, PackageScanCommand.Run(["--secrets", empty, "--secrets", emptySettings, "--require-secrets", clean]));   // local.json with values; settings.json may hold none

        // an unexpected crash is a refusal, never a pass
        try
        {
            PackageScanCommand.BeforeScanHook = _ => throw new InvalidOperationException("boom");
            Assert.Equal(2, PackageScanCommand.Run([clean]));
        }
        finally { PackageScanCommand.BeforeScanHook = null; }
        Assert.Equal(0, PackageScanCommand.Run([clean]));
        Assert.Equal(2, PackageScanCommand.Run(["--bogus", clean]));
        Assert.Equal(2, PackageScanCommand.Run([Path.Combine(_root, "missing.zip")]));
    }

    static byte[] ZipBytes(params (string Name, byte[] Data)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    static HashSet<string> BinaryProfile() => new(PackageScanCommand.BinaryProfileExtensions, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void NestedZipsAreScannedEntryByEntry()
    {
        // The tester zip carries the source zip in source/: game data inside it is found, with the nested path in the finding.
        byte[] inner = ZipBytes(("src-1/readme.md", Text("fine")), ("src-1/data/ebps.oppc", Text("harmless synthetic bytes")));
        var findings = Scan(Zip(("1 - READ ME FIRST.txt", Text("hello")), ("source/src-1.zip", inner)), s => s.SourceArchivePrefixes.Add("source/"));
        var hit = Assert.Single(findings);
        Assert.Equal("forbidden-extension", hit.Rule);
        Assert.Equal("source/src-1.zip!/src-1/data/ebps.oppc", hit.Entry);

        // Content rules apply inside too (an absolute user path in a nested text file).
        byte[] leaky = ZipBytes(("notes.txt", Text(Fix(@"C:\%U%\alice\game"))));
        Assert.Contains(Scan(Zip(("source/s.zip", leaky)), s => s.SourceArchivePrefixes.Add("source/")), f => f.Rule == "absolute-user-path" && f.Entry == "source/s.zip!/notes.txt");
    }

    [Fact]
    public void TheSourceArchiveIsExemptFromTheTypeAllowlistOnlyUnderItsPrefix()
    {
        byte[] source = ZipBytes(("s/Program.cs", Text("class P {}")), ("s/client/main.tscn", Text("[gd_scene]")));
        // Under the prefix: the zip and its source files pass the binary profile.
        Assert.Empty(Scan(Zip(("game/readme.txt", Text("ok")), ("source/s.zip", source)), s => { s.AllowedExtensions = BinaryProfile(); s.SourceArchivePrefixes.Add("source/"); }));
        // Elsewhere (or without the option) the zip is an unexpected type, and its entries are scanned with the binary profile too.
        var findings = Scan(Zip(("game/extra.zip", source)), s => { s.AllowedExtensions = BinaryProfile(); s.SourceArchivePrefixes.Add("source/"); });
        Assert.Contains(findings, f => f.Rule == "unexpected-type" && f.Entry == "game/extra.zip");
        Assert.Contains(findings, f => f.Rule == "unexpected-type" && f.Entry == "game/extra.zip!/s/Program.cs");
        // The profile is restored after a source archive: a later stray file is still refused.
        var after = Scan(Zip(("source/s.zip", source), ("zz/stray.cs", Text("x"))), s => { s.AllowedExtensions = BinaryProfile(); s.SourceArchivePrefixes.Add("source/"); });
        Assert.Contains(after, f => f.Rule == "unexpected-type" && f.Entry == "zz/stray.cs");
        Assert.DoesNotContain(after, f => f.Entry.StartsWith("source/", StringComparison.Ordinal));
    }

    [Fact]
    public void NestingIsBoundedAndABrokenNestedZipIsARefusal()
    {
        byte[] deepest = ZipBytes(("a.txt", Text("fine")));
        byte[] middle = ZipBytes(("deeper.zip", deepest));
        byte[] outer = ZipBytes(("middle.zip", middle));
        Assert.Contains(Scan(Zip(("outer.zip", outer))), f => f.Rule == "nested-too-deep");

        string broken = Zip(("source/s.zip", Text("not a zip at all, but named like one")));
        Assert.ThrowsAny<InvalidDataException>(() => Scan(broken, s => s.SourceArchivePrefixes.Add("source/")));
        Assert.Equal(2, PackageScanCommand.Run(["--source-archive", "source/", broken]));
        Assert.Equal(0, PackageScanCommand.Run(["--source-archive", "source/", "--profile", "binary", Zip(("source/s.zip", deepest))]));
    }

    [Fact]
    public void TheSourceOfferIsExactlyOneNamedEntry()
    {
        byte[] source = ZipBytes(("s/Program.cs", Text("class P {}")));
        const string exact = "source/sm-reimpl-source-abc1234.zip";
        void Exact(PackageScanner s) { s.AllowedExtensions = BinaryProfile(); s.SourceArchivePrefixes.Add(exact); }
        Assert.Empty(Scan(Zip((exact, source)), Exact));
        // Another zip in the same folder is not the source offer: an unexpected type, its files checked against the allowlist.
        var other = Scan(Zip((exact, source), ("source/extra.zip", source)), Exact);
        Assert.Contains(other, f => f.Rule == "unexpected-type" && f.Entry == "source/extra.zip");
        Assert.Contains(other, f => f.Rule == "unexpected-type" && f.Entry == "source/extra.zip!/s/Program.cs");
        // The same name twice (a zip may hold duplicate names): only the first is the offer.
        var twice = Scan(Zip((exact, source), (exact, source)), Exact);
        Assert.Contains(twice, f => f.Rule == "duplicate-source-archive");
        // A folder item without the slash is normalised: "source" does not match "sources/...".
        Assert.Contains(Scan(Zip(("sources/s.zip", source)), s => { s.AllowedExtensions = BinaryProfile(); s.SourceArchivePrefixes.Add("source"); }), f => f.Rule == "unexpected-type");
        Assert.Empty(Scan(Zip(("source/s.zip", source)), s => { s.AllowedExtensions = BinaryProfile(); s.SourceArchivePrefixes.Add("source"); }));
    }

    [Fact]
    public void ArchivesAreFoundByTheirMagicWhateverTheirName()
    {
        // A zip renamed to .dat is still opened and scanned.
        byte[] inner = ZipBytes(("data/ebps.oppc", Text("harmless synthetic bytes")));
        Assert.Contains(Scan(Zip(("game/readme.dat", inner))), f => f.Rule == "forbidden-extension" && f.Entry == "game/readme.dat!/data/ebps.oppc");
        // gzip, 7z and RAR cannot be looked into: refused.
        byte[] gzip = [0x1F, 0x8B, 0x08, 0, 0, 0, 0, 0, 0, 0];
        byte[] sevenZip = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0, 4];
        byte[] rar = Text("Rar!\u001A\u0007\u0000x");
        foreach (var data in new[] { gzip, sevenZip, rar })
            Assert.Contains(Scan(Zip(("game/notes.txt", data))), f => f.Rule == "unscannable-archive");
    }

    [Fact]
    public void TheSourceOfferHoldsNoArchivesOrBinariesButItsMedia()
    {
        void Source(PackageScanner s) => s.SourceArchivePrefixes.Add("source/s.zip");
        byte[] withZip = ZipBytes(("s/a.cs", Text("x")), ("s/more.zip", ZipBytes(("b.txt", Text("y")))));
        Assert.Contains(Scan(Zip(("source/s.zip", withZip)), Source), f => f.Rule == "archive-in-source" && f.Entry == "source/s.zip!/s/more.zip");
        byte[] withDll = ZipBytes(("s/tool.cs", [0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0]));
        Assert.Contains(Scan(Zip(("source/s.zip", withDll)), Source), f => f.Rule == "binary-in-source");
        byte[] withOgg = ZipBytes(("s/assets/bark.ogg", [0x4F, 0x67, 0x67, 0x53, 0, 2, 0, 0]), ("s/a.cs", Text("class A {}")));
        Assert.Empty(Scan(Zip(("source/s.zip", withOgg)), Source));
        // Outside the source offer the binary rule does not apply (a package's own .dll files are fine).
        Assert.Empty(Scan(Zip(("game/x.dll", [0x4D, 0x5A, 0x90, 0, 3, 0, 0, 0]))));
    }

    [Fact]
    public void ANestedZipIsBoundedByTheBytesItInflatesTo()
    {
        long saved = PackageScanner.MaxNestedBytes;
        try
        {
            PackageScanner.MaxNestedBytes = 4096;
            // A nested zip whose stated size is small (a crafted header) but whose stream delivers more: the bytes read decide.
            var noise = new byte[20_000];
            new Random(12345).NextBytes(noise);                  // incompressible, so the zip itself is large
            byte[] big = ZipBytes(("noise.txt", noise));
            Assert.True(big.Length > 4096);
            var findings = new List<ScanFinding>();
            new PackageScanner().ScanEntry("pkg.zip", "game/lies.zip", length: 100, () => new MemoryStream(big, writable: false), findings);
            Assert.Contains(findings, f => f.Rule == "nested-unverifiable" && f.Detail.Contains("inflates"));
            // The stated size alone is checked too.
            findings.Clear();
            new PackageScanner().ScanEntry("pkg.zip", "game/big.zip", length: big.Length, () => new MemoryStream(big, writable: false), findings);
            Assert.Contains(findings, f => f.Rule == "nested-unverifiable");
        }
        finally { PackageScanner.MaxNestedBytes = saved; }
    }
}
