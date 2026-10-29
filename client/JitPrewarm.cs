using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Godot;

namespace SmClient;

/// <summary>
/// M40-T9: compiles the methods of the given assemblies on a background thread (<see cref="RuntimeHelpers.PrepareMethod(RuntimeMethodHandle)"/>), so that
/// the first run of a rarely used path (the first shot, reaction, death, blast, look swap) does not stop the main thread for the JIT compiler.
/// Only methods of non-generic types without open generic parameters are prepared (the runtime compiles the rest as needed). The thread runs
/// below normal priority and stops at once when asked; a method that cannot be prepared is skipped. Nothing here touches the game state or Godot objects.
/// **Client performance aid, ours.**
/// </summary>
sealed class JitPrewarm
{
    readonly Assembly[] _assemblies;
    volatile bool _stop;
    int _prepared, _skipped;
    double _seconds;
    volatile bool _done;

    JitPrewarm(Assembly[] assemblies) => _assemblies = assemblies;

    public static JitPrewarm Start(params Assembly[] assemblies)
    {
        var prewarm = new JitPrewarm(assemblies);
        new Thread(prewarm.Run) { IsBackground = true, Name = "JitPrewarm", Priority = ThreadPriority.BelowNormal }.Start();
        return prewarm;
    }

    public void Stop() => _stop = true;

    public bool Done => _done;
    public string Report => $"{_prepared} methods compiled in the background in {_seconds:F1} s ({_skipped} skipped, {_skippedTypes} client types with static constructors left to the main thread){(_done ? "" : ", still running")}";

    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    void Run()
    {
        var watch = Stopwatch.StartNew();
        try
        {
            foreach (var assembly in _assemblies)
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = Array.FindAll(ex.Types, t => t is not null)!; }
                catch (Exception ex) when (ex is System.IO.IOException or BadImageFormatException or TypeLoadException or NotSupportedException or System.Security.SecurityException)
                {
                    GD.Print($"jit prewarm: {assembly.GetName().Name} skipped ({ex.GetType().Name}: {ex.Message})");
                    continue;
                }
                foreach (var type in types)
                {
                    if (_stop) return;
                    if (type.ContainsGenericParameters || type.IsInterface) continue;
                    if (SkipForStaticConstructor(type)) { _skippedTypes++; continue; }
                    try
                    {
                        foreach (var ctor in type.GetConstructors(All)) Prepare(ctor);
                        foreach (var method in type.GetMethods(All)) Prepare(method);
                    }
                    catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { _skipped++; }
                }
            }
        }
        catch (Exception ex)
        {
            // A background thread must never take the client down: say so and stop (what is compiled so far stays compiled).
            GD.PrintErr($"jit prewarm stopped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _seconds = watch.Elapsed.TotalSeconds;
            _done = true;
            GD.Print($"jit prewarm: {Report}");
        }
    }

    /// <summary>
    /// The JIT may run a type's static constructor while compiling a method that uses its statics, and this thread is not the main thread: the client's own types
    /// whose static constructors may call Godot (the OS, nodes, fonts, physics materials) are therefore left to the main thread. Compiler-generated closure classes
    /// (their constructor only makes the singleton) and the running <c>NetworkView</c> (its constructor has run) are exempt; the content, net, sim and format
    /// assemblies are plain managed code and are prepared as before.
    /// </summary>
    static bool SkipForStaticConstructor(Type type)
    {
        if (type.Assembly != typeof(JitPrewarm).Assembly) return false;
        if (type.TypeInitializer is null) return false;
        for (var t = type; t is not null; t = t.DeclaringType)
        {
            if (t == typeof(NetworkView)) return false;
            if (t.IsDefined(typeof(CompilerGeneratedAttribute), false)) return false;
        }
        return true;
    }

    int _skippedTypes;

    void Prepare(MethodBase method)
    {
        if (_stop) return;
        if (method.IsAbstract || method.ContainsGenericParameters || (method.GetMethodImplementationFlags() & MethodImplAttributes.InternalCall) != 0) return;
        try { RuntimeHelpers.PrepareMethod(method.MethodHandle); _prepared++; }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { _skipped++; }
    }
}
