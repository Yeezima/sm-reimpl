using System.Text.Json;

namespace SmContent.Extras.LoyalistBarks;

/// <summary>The pack's own event labels (its <c>suggested_event</c>): what a phrase suits, not an engine event.</summary>
public enum BarkEvent : byte { BattleCry = 1, Attack = 2, Firing = 3, UnderFire = 4 }

/// <summary>One take of a phrase: <c>loyalist_for_the_emperor_02</c> is take 2 of <c>for_the_emperor</c>.</summary>
public sealed record BarkClip(string Id, string File, string Text, string Phrase, BarkEvent Event, int Take, double Seconds, string Sha256);

/// <summary>
/// Added content "loyalist barks" (the owner, 2026-10-29): the owner's own voice pack of 32 takes (8 phrases x 4), synthesized with ElevenLabs
/// with the helmet filter baked in, not extracted from any game. The clips and the slimmed manifest (<c>loyalist-barks.json</c>) live in
/// <c>client/Extras/LoyalistBarks/assets/</c> and are embedded in the client assembly only when added content is built. This reads the manifest
/// (engine-free; tested against the committed file).
/// </summary>
public sealed class LoyalistBarkPack
{
    /// <summary>The battle-cry key's phrase: every take of it, and nothing else.</summary>
    public const string EmperorPhrase = "for_the_emperor";

    public IReadOnlyList<BarkClip> Clips { get; }
    public int SampleRate { get; }

    LoyalistBarkPack(List<BarkClip> clips, int rate) { Clips = clips; SampleRate = rate; }

    public static BarkEvent? ParseEvent(string? name) => name switch
    {
        "battle_cry" => BarkEvent.BattleCry,
        "attack" => BarkEvent.Attack,
        "firing" => BarkEvent.Firing,
        "under_fire" => BarkEvent.UnderFire,
        _ => null,
    };

    /// <summary>Reads the manifest; throws <see cref="InvalidDataException"/> for a malformed one (a clip without a known event, take or file, or an id twice).</summary>
    public static LoyalistBarkPack Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("schema_version").GetInt32() != 1) throw new InvalidDataException("unknown loyalist bark manifest schema");
            int rate = root.GetProperty("format").GetProperty("sample_rate").GetInt32();
            var clips = new List<BarkClip>();
            foreach (var c in root.GetProperty("clips").EnumerateArray())
            {
                string id = c.GetProperty("id").GetString() ?? "";
                var evt = ParseEvent(c.GetProperty("event").GetString()) ?? throw new InvalidDataException($"clip {id}: unknown event");
                string file = c.GetProperty("file").GetString() ?? "";
                if (id.Length == 0 || !file.StartsWith("audio/", StringComparison.Ordinal) || file.Contains("..", StringComparison.Ordinal) || !file.EndsWith(".ogg", StringComparison.Ordinal))
                    throw new InvalidDataException($"clip '{id}': bad file '{file}'");
                int take = c.GetProperty("take").GetInt32();
                double seconds = c.GetProperty("duration_seconds").GetDouble();
                if (take < 1 || seconds <= 0 || seconds > 10) throw new InvalidDataException($"clip {id}: take {take}, {seconds} s");
                if (clips.Any(x => x.Id == id)) throw new InvalidDataException($"clip {id} twice");
                clips.Add(new BarkClip(id, file, c.GetProperty("text").GetString() ?? "", c.GetProperty("phrase").GetString() ?? "", evt, take, seconds,
                    c.TryGetProperty("sha256", out var h) ? h.GetString() ?? "" : ""));
            }
            return new LoyalistBarkPack(clips, rate);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("malformed loyalist bark manifest: " + e.Message, e);
        }
    }
}
