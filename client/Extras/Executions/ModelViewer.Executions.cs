using System.Collections.Generic;

namespace SmClient;

public partial class ModelViewer
{
    /// <summary><c>--execution-test [&lt;row id&gt;]</c> (executions X7): an execution's attacker and victim clips from the install, played together (<see cref="ExecutionTestView"/>).</summary>
    partial void ExtrasExecutionTest(string gameDir, Dictionary<string, string?> args, ref bool handled)
    {
        if (!args.ContainsKey("execution-test")) return;
        AddChild(new ExecutionTestView(gameDir, args) { Name = "ExecutionTest" });
        handled = true;
    }
}
