using System.Diagnostics;
using System.Globalization;
using SmContent;

namespace SmServer;

/// <summary>A bot process the developer tools started (<see cref="IDevBotLauncher"/>).</summary>
public interface IDevBotProcess
{
    /// <summary>The process id, for the log.</summary>
    int Id { get; }
    /// <summary>Whether the process is still running.</summary>
    bool Running { get; }
    /// <summary>Stops the process (and the Godot child the console wrapper starts) and nothing else. Safe to call twice.</summary>
    void Stop();
}

/// <summary>Starts the test bots the developer tools add (a headless <c>client --bot</c> process of this machine, on the server's own loopback address).</summary>
public interface IDevBotLauncher
{
    /// <summary>Starts a bot for the slot, whose Hello carries <paramref name="token"/> as its nonce (the server ties the process to its player by it); null when it could not be started (the reason goes to <paramref name="error"/>).</summary>
    IDevBotProcess? Start(BotSlot slot, uint token, out string error);
}

/// <summary>
/// The real launcher: the Godot console executable (or an exported game's console build) with the headless bot arguments of <see cref="HostBots.ClientArguments"/>, connected to
/// <c>127.0.0.1:port</c>. Each process is held by its handle; only those are ever stopped (never a process found by name or CPU), and each one's output goes to its own log file in
/// <paramref name="logFolder"/> (not the server's console). A bot also quits by itself a few seconds after its server is gone, so none outlives a server that exits.
/// </summary>
public sealed class ProcessBotLauncher(string exe, string? projectPath, int port, string? content, string logFolder) : IDevBotLauncher
{
    int _started;

    public IDevBotProcess? Start(BotSlot slot, uint token, out string error)
    {
        error = "";
        string logPath;
        try
        {
            Directory.CreateDirectory(logFolder);
            logPath = Path.Combine(logFolder, string.Create(CultureInfo.InvariantCulture, $"devbot-{++_started}-{slot.Side}.log"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = $"no log folder: {e.Message}";
            return null;
        }
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        };
        foreach (var a in HostBots.ClientArguments(port, slot, content, projectPath, logPath + ".godot")) info.ArgumentList.Add(a);
        info.ArgumentList.Add("--dev-bot-token"); info.ArgumentList.Add(token.ToString(CultureInfo.InvariantCulture));   // the client's Hello nonce (NetworkView.Demo.cs)
        try
        {
            var process = Process.Start(info);
            if (process is null) { error = "the process did not start"; return null; }
            DevBotJob.TryAssign(process);   // kill-on-close job: the bot dies with the server even on a hard kill or a crash
            return new ChildBot(process, logPath);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            error = e.Message;
            return null;
        }
    }

    sealed class ChildBot : IDevBotProcess
    {
        readonly Process _process;
        readonly StreamWriter? _log;
        readonly object _lock = new();
        bool _closed;

        public ChildBot(Process process, string logPath)
        {
            _process = process;
            Id = process.Id;
            try { _log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log = null; }
            process.OutputDataReceived += (_, e) => Write(e.Data);
            process.ErrorDataReceived += (_, e) => Write(e.Data);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        void Write(string? line)
        {
            if (line is null) return;
            lock (_lock) { if (!_closed) try { _log?.WriteLine(line); } catch (Exception e) when (e is IOException or ObjectDisposedException) { } }
        }

        public int Id { get; }

        public bool Running { get { try { return !_process.HasExited; } catch (InvalidOperationException) { return false; } } }

        public void Stop()
        {
            try { if (!_process.HasExited) { _process.Kill(entireProcessTree: true); _process.WaitForExit(3000); } }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            lock (_lock) { _closed = true; try { _log?.Dispose(); } catch (IOException) { } }
            try { _process.Dispose(); } catch (Exception) { }
        }
    }

    /// <summary>
    /// Where the bots' executable and Godot project are. The executable: <paramref name="exeArg"/> (<c>--dev-bot-exe</c>), else the Godot console build the repository's tools download
    /// (<c>%LOCALAPPDATA%\sm-reimpl\tools\godot\...</c>); null when neither exists. The project folder: <paramref name="projectArg"/> (<c>--dev-bot-project</c>), else the
    /// <c>client</c> folder of the checkout this server runs from (found by walking up from <paramref name="startDir"/>); null for an exported game (it needs none).
    /// </summary>
    public static (string? Exe, string? Project) Locate(string? exeArg, string? projectArg, string? startDir)
    {
        string? exe = null;
        if (!string.IsNullOrWhiteSpace(exeArg)) exe = File.Exists(exeArg) ? Path.GetFullPath(exeArg) : null;
        else
        {
            string local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            const string name = "Godot_v4.7.2-stable_mono_win64";
            string candidate = Path.Combine(local, "sm-reimpl", "tools", "godot", name, name + "_console.exe");
            if (local.Length > 0 && File.Exists(candidate)) exe = candidate;
        }
        string? project = null;
        if (!string.IsNullOrWhiteSpace(projectArg)) project = Directory.Exists(projectArg) ? Path.GetFullPath(projectArg) : null;
        else if (exe is not null && !string.IsNullOrWhiteSpace(startDir))
        {
            for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "client", "project.godot"))) { project = Path.Combine(dir.FullName, "client"); break; }
        }
        return (exe, project);
    }

    /// <summary>The folder the bots' logs go to: <c>%LOCALAPPDATA%\sm-reimpl\tmp-build\dev-bots</c> (outside the repository and the install).</summary>
    public static string DefaultLogFolder() =>
        Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local ? local : Path.GetTempPath(), "sm-reimpl", "tmp-build", "dev-bots");
}
