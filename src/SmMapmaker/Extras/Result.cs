using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>A failed operation: a stable <see cref="Code"/> (docs/mapmaker.md), a message, the operation's name and the offending object id when there is one.</summary>
/// <param name="Index">Position of the operation in its batch (an addition to the contract).</param>
public sealed record OpError(string Code, string Message, string Op, string? Id = null)
{
    public int? Index { get; init; }

    public JsonObject ToNode()
    {
        var o = new JsonObject { ["code"] = Code, ["message"] = Message, ["op"] = Op };
        if (Id is not null) o["id"] = Id;
        if (Index is { } i) o["index"] = i;
        return o;
    }
}

/// <summary>
/// The outcome of a batch: <c>{ ok, created:[ids], warnings:[...], errors:[...] }</c>. A batch is one transaction, so when <see cref="Ok"/> is false nothing changed.
/// </summary>
public sealed record OpResult(bool Ok, IReadOnlyList<string> Created, IReadOnlyList<string> Warnings, IReadOnlyList<OpError> Errors)
{
    /// <summary>Prefab ids made by the batch (group-to-prefab, extract-sm1, import-prefab).</summary>
    public IReadOnlyList<string> CreatedPrefabs { get; init; } = [];
    /// <summary>Asset ids imported by the batch.</summary>
    public IReadOnlyList<string> CreatedAssets { get; init; } = [];
    /// <summary>For duplicate: every copied object (old id, new id), including the copied descendants.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Mapping { get; init; } = [];
    /// <summary>The report of the last refresh-from-classic in the batch (added, removed, changed, deleted, skipped); null otherwise.</summary>
    public JsonObject? Refresh { get; init; }
    public bool DryRun { get; init; }

    public static OpResult Fail(string code, string message, string op = "", string? id = null) =>
        new(false, [], [], [new OpError(code, message, op, id)]);

    public JsonObject ToNode()
    {
        static JsonArray Strings(IEnumerable<string> s) => new(s.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
        var o = new JsonObject
        {
            ["ok"] = Ok, ["created"] = Strings(Created), ["warnings"] = Strings(Warnings),
            ["errors"] = new JsonArray(Errors.Select(e => (JsonNode)e.ToNode()).ToArray()),
        };
        if (CreatedPrefabs.Count > 0) o["createdPrefabs"] = Strings(CreatedPrefabs);
        if (CreatedAssets.Count > 0) o["createdAssets"] = Strings(CreatedAssets);
        if (Mapping.Count > 0)
        {
            var m = new JsonObject();
            foreach (var (k, v) in Mapping) m[k] = v;
            o["mapping"] = m;
        }
        if (Refresh is not null) o["refresh"] = Refresh.DeepClone();
        if (DryRun) o["dryRun"] = true;
        return o;
    }

    public string ToJson() => CanonicalJson.Write(ToNode());
}
