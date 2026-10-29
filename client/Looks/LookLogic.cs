using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SmContent.Customization;
using SmNet;
using SmSim;

namespace SmClient.Looks;

/// <summary>
/// The engine-free rules of the networked view's looks (customizer T7, docs/customizer-architecture.md sections 5 and 8): which key names a model, when a character
/// may change its model, how much building a frame may spend, which look the own player wears and what the loadout picker says. The Godot code
/// (<c>NetworkView.Appearance.cs</c>, <c>NetworkView.Loadout.cs</c>) only calls these; tests link this file (no Godot types here).
/// </summary>
public static class LookKeys
{
    /// <summary>
    /// The name of what a character wears: race, class and the resolved look's content (pieces, paint, decals, lens), plus whether the custom emblem is here yet. Two inputs
    /// that resolve to the same drawing (a different weapon on the same armour) share a key, so nothing is rebuilt for them. Docs: the architecture's key is
    /// <c>race|classKey|appearance version</c>; the content form is the same identity without needing the server's counter (the own look has none).
    /// </summary>
    public static string Of(ResolvedAppearance look, bool emblemReady)
    {
        var sb = new StringBuilder(256);
        sb.Append(look.Race).Append('|').Append(look.ClassKey).Append('|').Append(look.PredefinedSet ?? "").Append('|');
        foreach (var piece in look.Pieces) sb.Append(piece.Slot).Append(':').Append(piece.Name).Append(',');
        sb.Append('|');
        foreach (var (slot, paint) in look.Paint.OrderBy(p => p.Key, StringComparer.Ordinal))
            sb.Append(slot).Append(':').Append(paint.Primary?.Name).Append('/').Append(paint.Secondary?.Name).Append('/').Append(paint.Tertiary?.Name).Append(paint.PatternEnabled ? "+" : "-").Append(',');
        sb.Append('|');
        foreach (var decal in new[] { look.LeftDecal, look.RightDecal })
            sb.Append(decal.Kind).Append(':').Append(decal.Badge?.Texture).Append(':').Append(decal.Emblem.IsNone ? "" : decal.Emblem.ToHex()).Append(',');
        sb.Append('|');
        if (look.Lens is { } lens) sb.Append(lens.R).Append('.').Append(lens.G).Append('.').Append(lens.B);
        sb.Append(emblemReady ? "|e" : "|-");
        return sb.ToString();
    }

    /// <summary>The key of the input a resolve depends on (cache key for the resolved look): race, class, the wire code and the carried wargear.</summary>
    public static string InputOf(string race, string classKey, AppearanceCode code, string carriedKey) => $"{race}|{classKey}|{code}|{carriedKey}";

    /// <summary>The wargear and perk names a player carries, neutral-sorted and joined: part of <see cref="InputOf"/>.</summary>
    public static string CarriedKey(IEnumerable<string> carried) =>
        string.Join(',', carried.Select(c => ProfileWire.Neutral(c).ToLowerInvariant()).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal));

    /// <summary>The key of a placeholder character (the default marine drawn while the real look is being built).</summary>
    public const string Placeholder = "placeholder";
}

/// <summary>What a character does about its model this frame.</summary>
public enum LookAction { Keep, Create, Swap }

/// <summary>When a character's model may change (customizer T7): never wait, never swap a dead body.</summary>
public static class SwapRules
{
    /// <summary>
    /// A character with no model gets one at once (the placeholder when the wanted look is not ready: nothing waits). A character keeps its model until the wanted one
    /// is ready, and swaps only while alive and not just dead (a body being turned into a ragdoll, or lying dead, keeps its model: corpses keep theirs).
    /// </summary>
    public static LookAction Decide(string? currentKey, string wantedKey, bool wantedReady, bool alive, bool deathPending)
    {
        if (currentKey is null) return LookAction.Create;
        if (currentKey == wantedKey) return LookAction.Keep;
        if (!wantedReady) return LookAction.Keep;
        if (!alive || deathPending) return LookAction.Keep;
        return LookAction.Swap;
    }

    /// <summary>
    /// A race change at the respawn (Ork O7a; marine to Ork or back): when the character's rig is not the wanted race's and the wanted model is not ready, the character must not keep the old
    /// rig (a marine drawn and posed as an Ork, or an Ork as a marine): it is replaced by the placeholder (the default marine) at once. Only a living character that is not just dead is
    /// replaced (corpses keep theirs); a marine that already is the placeholder stays while an Ork is awaited.
    /// </summary>
    public static bool DropWrongRig(bool currentIsOrkRig, bool wantsOrk, string? currentKey, bool alive, bool deathPending) =>
        currentKey is not null && alive && !deathPending && currentIsOrkRig != wantsOrk && (currentIsOrkRig || currentKey != LookKeys.Placeholder);
}

/// <summary>
/// Spreads model building over frames: looks that are wanted are tried in priority order (the own player first, then the order they were first wanted) until the
/// frame's budget is spent; at least one is tried per frame, so something always progresses. A look nobody wants any more is dropped.
/// </summary>
public sealed class BuildScheduler
{
    sealed class Entry { public int Priority; public long Order; public long Seen; }

    readonly Func<double> _nowMs;
    readonly Dictionary<string, Entry> _wanted = new(StringComparer.Ordinal);
    long _frame, _order;

    public double BudgetMs { get; }
    public int Attempts { get; private set; }
    public int Deferred { get; private set; }
    public int Pending => _wanted.Count;
    public bool IsPending(string key) => _wanted.ContainsKey(key);

    public BuildScheduler(Func<double> nowMs, double budgetMs) { _nowMs = nowMs; BudgetMs = budgetMs; }

    /// <summary>Says this frame that <paramref name="key"/> is wanted (lower <paramref name="priority"/> first). Cheap; call it every frame while it is.</summary>
    public void Want(string key, int priority)
    {
        if (!_wanted.TryGetValue(key, out var e)) { _wanted[key] = new Entry { Order = _order++, Priority = priority, Seen = _frame }; return; }
        // Wanted twice in one frame (the own player and another in the same look): the most urgent priority counts.
        e.Priority = e.Seen == _frame ? Math.Min(e.Priority, priority) : priority; e.Seen = _frame;
    }

    /// <summary>
    /// End of frame: tries the wanted looks with <paramref name="step"/> (true: finished, the look is not wanted again) while the budget lasts. Returns the keys finished.
    /// Looks not wanted this frame are forgotten first.
    /// </summary>
    public List<string> Run(Func<string, bool> step)
    {
        var done = new List<string>();
        foreach (var gone in _wanted.Where(p => p.Value.Seen != _frame).Select(p => p.Key).ToList()) _wanted.Remove(gone);
        double start = _nowMs();
        int tried = 0;
        foreach (var key in _wanted.OrderBy(p => p.Value.Priority).ThenBy(p => p.Value.Order).Select(p => p.Key).ToList())
        {
            if (tried > 0 && _nowMs() - start >= BudgetMs) { Deferred++; break; }
            tried++; Attempts++;
            if (step(key)) { done.Add(key); _wanted.Remove(key); }
        }
        _frame++;
        return done;
    }
}

/// <summary>Whether a custom emblem's bytes are here (asked at most twice a second per hash; an arrival is noted at once).</summary>
public sealed class EmblemAvailability
{
    readonly HashSet<EmblemHash> _have = new();
    readonly Dictionary<EmblemHash, double> _next = new();
    readonly Func<EmblemHash, bool> _probe;
    readonly Func<double> _seconds;

    public EmblemAvailability(Func<EmblemHash, bool> probe, Func<double> seconds) { _probe = probe; _seconds = seconds; }

    public bool Has(EmblemHash hash)
    {
        if (hash.IsNone || _have.Contains(hash)) return true;
        double now = _seconds();
        if (_next.TryGetValue(hash, out var at) && now < at) return false;
        _next[hash] = now + 0.5;
        if (!_probe(hash)) return false;
        _have.Add(hash);
        return true;
    }

    public void Arrived(EmblemHash hash) => _have.Add(hash);
    public int Count => _have.Count;
}

/// <summary>
/// The own player's look: the codes the Hello carried, and a change sent with <c>ChangeSetup</c> that the server applies at the next respawn. The own model changes at the same
/// moment: the respawn after the server acknowledged the request, or once the scoreboard shows the acknowledged version for the own row.
/// </summary>
public sealed class OwnLooks
{
    AppearanceCode _spaceMarine, _chaos;
    (AppearanceCode SpaceMarine, AppearanceCode Chaos)? _pending;
    bool _acked;
    ushort _ackVersion;
    bool _wasAlive = true;
    long _updates, _ackedAtUpdate;

    public OwnLooks(AppearanceCode spaceMarine, AppearanceCode chaos) { _spaceMarine = spaceMarine; _chaos = chaos; }

    public AppearanceCode Active(Team race) => race == Team.Chaos ? _chaos : _spaceMarine;
    public (AppearanceCode SpaceMarine, AppearanceCode Chaos)? Pending => _pending;
    public bool Acked => _acked;

    /// <summary>A setup was sent: it is pending until the respawn (the older pending one is replaced).</summary>
    public void Request(AppearanceCode spaceMarine, AppearanceCode chaos) { _pending = (spaceMarine, chaos); _acked = false; _ackVersion = 0; }

    /// <summary>The server answered the pending request (its appearance version is what the scoreboard will show for the own row once applied).</summary>
    public void Acknowledged(ushort version, bool emblemRejected)
    {
        if (_pending is not { } p) return;
        _acked = true; _ackVersion = version; _ackedAtUpdate = _updates;
        // The server shows the fallback badge when it refused the emblem; so does the own model (the same look as everyone sees).
        if (emblemRejected) _pending = (Appearances.WithoutEmblem(p.SpaceMarine), Appearances.WithoutEmblem(p.Chaos));
    }

    /// <summary>Every frame: <paramref name="alive"/> is the own player's state, <paramref name="ownScoreboardVersion"/> the version on the scoreboard's own row (0: the default look; <paramref name="rowPresent"/> says the row exists, a missing row also reads 0). Returns true when the pending look became the active one.</summary>
    public bool Update(bool alive, ushort ownScoreboardVersion, bool rowPresent = true)
    {
        _updates++;
        bool respawned = alive && !_wasAlive;
        _wasAlive = alive;
        if (_pending is not { } p || !_acked) return false;
        // A respawn counts only when the acknowledgement came in an earlier frame: a Setup that reached the server within a round trip of the respawn
        // may have missed it (it then applies a life later); the scoreboard's version tells in that case.
        bool ackedBefore = _ackedAtUpdate < _updates - 1;   // the ack is noted before the frame's Update, so "this frame" is _updates - 1
        // The server gives the default look version 0 (also in its ack): then the own row showing 0 is the proof, and only while the look shown is not the default one.
        bool byVersion = alive && (_ackVersion != 0 ? ownScoreboardVersion == _ackVersion : rowPresent && ownScoreboardVersion == 0 && !(_spaceMarine.IsDefault && _chaos.IsDefault));
        if (!((respawned && ackedBefore) || byVersion)) return false;
        _spaceMarine = p.SpaceMarine; _chaos = p.Chaos; _pending = null; _acked = false;
        return true;
    }
}

/// <summary>Counts the frames a look change may have stretched (the frame after a frame that built, swapped or processed an arrival).</summary>
public sealed class HitchMeter
{
    public const double LimitMs = 33.0;
    bool _lastWork;
    public int Frames { get; private set; }
    public int WorkFrames { get; private set; }
    public int OverLimit { get; private set; }
    public int OverLimitAfterWork { get; private set; }
    public double MaxMs { get; private set; }
    public double MaxAfterWorkMs { get; private set; }

    /// <summary>One frame: its length, and whether look work (a build step, a swap, an arrival) happened in it.</summary>
    public void Add(double frameMs, bool work)
    {
        Frames++;
        MaxMs = Math.Max(MaxMs, frameMs);
        if (frameMs > LimitMs) OverLimit++;
        if (_lastWork)
        {
            WorkFrames++;
            MaxAfterWorkMs = Math.Max(MaxAfterWorkMs, frameMs);
            if (frameMs > LimitMs) OverLimitAfterWork++;
        }
        _lastWork = work;
    }

    public string Summary() => $"{Frames} frames, max {MaxMs:F1} ms, {OverLimit} over {LimitMs:F0} ms; after look work: {WorkFrames} frames, max {MaxAfterWorkMs:F1} ms, {OverLimitAfterWork} over {LimitMs:F0} ms";
}

/// <summary>What the in-match loadout picker allows and says (docs/customizer-design.md F2, acceptance M1 to M3).</summary>
public static class PickerRules
{
    /// <summary>C opens the picker while the player is down: dead, spectating, or after the match; never while alive in play.</summary>
    public static bool CanOpen(bool connected, bool alive, bool spectating, bool matchEnded) => connected && (!alive || spectating || matchEnded);

    /// <summary>What C says while alive (the Esc menu is the way to change the loadout then).</summary>
    public const string WhileAlive = "Press Esc to change your loadout";

    /// <summary>What Esc does in a match (customizer menu, M44).</summary>
    public enum EscAction { None, BackToMenu, OpenMenu }

    /// <summary>
    /// Esc in the networked view. The front end's back path keeps priority while the join is going nowhere (still connecting, rejected, disconnected, load error). Once connected, alive or
    /// dead, Esc opens the customizer menu. While the menu is open it owns the key (the creator closes it with its own Esc, see <see cref="EscClosesFirst"/>), so the view does nothing.
    /// Replays and bots have no menu.
    /// </summary>
    public static EscAction OnEscape(bool connected, bool connectFailing, bool menuOpen, bool human)
    {
        if (connectFailing) return EscAction.BackToMenu;
        if (!connected || !human || menuOpen) return EscAction.None;
        return EscAction.OpenMenu;
    }

    /// <summary>What an Esc press inside the open menu closes first (the order the handlers realize: a dialog before a sub-screen before the menu).</summary>
    public enum EscLevel { Dialog, SubScreen, Menu }
    public static EscLevel EscClosesFirst(bool dialogOpen, bool subScreenOpen) => dialogOpen ? EscLevel.Dialog : subScreenOpen ? EscLevel.SubScreen : EscLevel.Menu;

    /// <summary>What LEAVE MATCH asks first: unsaved edits go through the same Save / Discard / Cancel prompt as RESUME, else a plain confirmation.</summary>
    public enum LeavePrompt { Confirm, SaveFirst }
    public static LeavePrompt LeaveStep(bool unsavedEdits) => unsavedEdits ? LeavePrompt.SaveFirst : LeavePrompt.Confirm;

    /// <summary>Alive in a live match the menu shows the match behind a dim; on the death screen and at the match's end it is opaque.</summary>
    public static bool Translucent(bool alive, bool matchEnded) => alive && !matchEnded;

    /// <summary>Whether leaving the menu captures the mouse again: only for a human in a connected match that is not being left.</summary>
    public static bool RecaptureMouse(bool human, bool testWindow, bool connected, bool leaving) => human && !testWindow && connected && !leaving;

    public static string Banner(bool alive, float respawnLeft) =>
        alive ? "Changes apply when you respawn. You are still in the match: it goes on while this is open."
              : $"Applies when you respawn - RESPAWN IN {Math.Max(0, (int)MathF.Ceiling(respawnLeft))}";

    /// <summary>
    /// The death screen's line about the last change: pending, accepted, accepted with defaulted fields, or the emblem trouble. <paramref name="acceptedClass"/> (the class
    /// the ack names, display name) is said when known; <paramref name="sentClass"/> differing from it means the server played the request on the player's side by role
    /// (a class of the other side, e.g. after a side change): the line says so.
    /// </summary>
    public static (string Text, bool Problem) AckText(bool pending, double waitedSeconds, SetupAck? ack, bool sent, string? acceptedClass = null, string? sentClass = null, string? sideName = null)
    {
        if (!sent) return ("", false);
        if (pending) return waitedSeconds > 3 ? ("The server has not answered yet; still trying", true) : ("Sending your loadout...", false);
        if (ack is null) return ("", false);
        var f = ack.Flags;
        string what = string.IsNullOrEmpty(acceptedClass) ? "Loadout" : acceptedClass;
        string text = $"{what} accepted: applies when you respawn";
        bool problem = false;
        if (!string.IsNullOrEmpty(acceptedClass) && !string.IsNullOrEmpty(sentClass) && !string.Equals(acceptedClass, sentClass, StringComparison.OrdinalIgnoreCase))
        {
            text = $"{sentClass} is not a class of {(sideName is null ? "your side" : "the " + sideName)}: you respawn as {acceptedClass}, its counterpart";
            problem = true;
        }
        if ((f & SetupFlags.Defaulted) != 0 && !problem) { text = $"{what} accepted, but some choices were not valid and use their defaults"; problem = true; }
        if ((f & SetupFlags.EmblemRejected) != 0) { text += "; your emblem was refused, the badge shows instead"; problem = true; }
        else if ((f & SetupFlags.EmblemUploading) != 0) text += "; your emblem is uploading";
        return (text, problem);
    }

    /// <summary>
    /// What SET ACTIVE / DEPLOY say in a match once the loadout is sent (fix/class-change-in-match): the class the player respawns as on its side. The creator's role tabs
    /// carry both sides' names ("Devastator / Havoc") and its preview can show the other side; a loadout of the other side's class name plays as this side's class of the
    /// same role, and the line says so instead of leaving the player to find out at the respawn.
    /// </summary>
    public static string ActivatedText(string playedClass, string sideName, string? shownClass, string? shownSideName)
    {
        if (!string.IsNullOrEmpty(shownClass) && !string.Equals(shownClass, playedClass, StringComparison.OrdinalIgnoreCase))
            return $"{shownClass} is a {shownSideName ?? "other side"} class: on the {sideName} side you respawn as {playedClass} (same role). Rejoin on the {shownSideName ?? "other"} side to play {shownClass}.";
        return $"{playedClass} is your active loadout: you respawn as {playedClass}.";
    }

    /// <summary>
    /// Whether closing the menu sends the active loadout (fix/class-change-in-match): only when the saved active loadout differs from what was in effect when the menu opened
    /// or was last sent (the player changed or edited the active loadout and saved, without DEPLOY or SET ACTIVE), never when the menu was closed by a deploy, when
    /// leaving the match or when not connected. Merely opening and closing the menu sends nothing: a match joined with <c>--class</c> keeps that class.
    /// </summary>
    public static bool SendOnClose(bool deployed, bool leaving, bool connected, LoadoutKey? inEffect, LoadoutKey? activeNow) =>
        !deployed && !leaving && connected && inEffect is { } before && activeNow is { } now && !before.Equals(now);

    /// <summary>
    /// What counts as in effect after a loadout was sent (fix/set-active-save-failure): the loadout sent, unless saving it as the profile's active loadout failed (a read-only
    /// or unreadable profile, an I/O error). Then the saved profile still names the old active loadout, and taking that as in effect keeps <see cref="SendOnClose"/> from
    /// re-sending the old loadout when the menu closes (which undid SET ACTIVE). The change sent still applies at the next respawn of this match.
    /// </summary>
    public static LoadoutKey? InEffectAfterSend(LoadoutKey sent, LoadoutKey? savedActive, string? saveError) => saveError is null ? sent : savedActive;

    /// <summary>The line added when the loadout was sent but could not be saved in the profile.</summary>
    public static string NotSavedText(string error) =>
        $"Not saved in your profile ({error}): it applies in this match only; the profile keeps its old active loadout.";

    /// <summary>The line after a respawn that changed the own class (shown a few seconds): empty when the class stayed.</summary>
    public static string RespawnText(int classBefore, int classAfter, string classAfterName) =>
        classBefore >= 0 && classAfter >= 0 && classBefore != classAfter ? $"You are now playing {classAfterName}" : "";
}

/// <summary>What a loadout sends, for comparing (<see cref="PickerRules.SendOnClose"/>): the wire loadout and both races' looks.</summary>
public readonly record struct LoadoutKey(LoadoutChoice Choice, AppearanceCode SpaceMarine, AppearanceCode Chaos);
