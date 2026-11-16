namespace SmNet;

/// <summary>What a server hit report still shows (<see cref="PredictedHitLedger.Confirm"/>): its hit marker, and its ranged hit confirmation sound.</summary>
public readonly record struct ReportFeedback(bool Marker, bool Sound);

/// <summary>
/// net/responsive (ours; client presentation only, the server still decides every hit and its damage): the own predicted ranged hits. When the own predicted hit-scan
/// shot's ray strikes an enemy as this client draws it (the trace the tracer and impact already use), the hit marker and the hit confirmation sound play at once
/// (<see cref="Predict"/>) instead of a round trip, the input lead and a snapshot later. The server's report that follows (<see cref="Confirm"/>) is matched against the
/// predictions still pending (at most <see cref="Window"/> old; the report's victim first, then any): hits already shown play nothing again. The report's marker shows
/// when it has hits no prediction showed, a kill (the kill marker) or a headshot the prediction did not show as one; its hit confirmation sound plays only for hits no
/// prediction showed (review M1: a kill or an upgrade to a headshot played the weapon's smack a second time; the data's kill layer is a layer of that same event, picked
/// by the death state, so it cannot play alone). Melee and blast reports are not ranged predictions and always play. A prediction no report confirms within the window expires (<see cref="Unconfirmed"/>: a marker shown for a shot the
/// server judged a miss).
/// </summary>
public sealed class PredictedHitLedger
{
    /// <summary>How long a prediction waits for the server's report (seconds; ours: the round trip, lead and snapshot wait of a 250 ms link with room).</summary>
    public const double Window = 0.75;
    /// <summary>The most predictions kept (the oldest go first).</summary>
    public const int MaxPending = 32;

    readonly List<(int Victim, bool Head, double At)> _pending = new();

    /// <summary>Predictions shown so far; of them confirmed by a report, and expired unconfirmed (diagnostics, the readout).</summary>
    public int Predicted { get; private set; }
    public int Confirmed { get; private set; }
    public int Unconfirmed { get; private set; }

    /// <summary>Predictions still waiting for a report.</summary>
    public int Pending => _pending.Count;

    /// <summary>An own predicted ranged hit on <paramref name="victim"/> was shown now.</summary>
    public void Predict(int victim, bool head, double now)
    {
        Expire(now);
        _pending.Add((victim, head, now));
        Predicted++;
        if (_pending.Count > MaxPending) { _pending.RemoveAt(0); Unconfirmed++; }
    }

    /// <summary>
    /// The server's report of <paramref name="hits"/> hits arrived now: whether it still shows its hit marker (<see cref="ReportFeedback.Marker"/>: hits no prediction
    /// showed, a kill or an unpredicted headshot) and whether it plays the ranged hit confirmation (<see cref="ReportFeedback.Sound"/>: only hits no prediction showed).
    /// </summary>
    public ReportFeedback Confirm(int hits, int victim, bool head, bool kill, bool melee, bool splash, double now)
    {
        Expire(now);
        if (melee || splash || hits <= 0) return new(true, true);
        int matched = 0;
        bool headShown = false;
        for (int pass = 0; pass < 2 && matched < hits; pass++)
            for (int i = 0; i < _pending.Count && matched < hits; i++)
            {
                if (pass == 0 && _pending[i].Victim != victim) continue;
                headShown |= _pending[i].Victim == victim && _pending[i].Head;
                _pending.RemoveAt(i--);
                matched++;
            }
        Confirmed += matched;
        bool unpredicted = matched < hits;
        return new(unpredicted || kill || (head && !headShown), unpredicted);
    }

    /// <summary>Drops predictions older than <see cref="Window"/> (counted as unconfirmed).</summary>
    public void Expire(double now)
    {
        int before = _pending.Count;
        _pending.RemoveAll(p => now - p.At > Window);
        Unconfirmed += before - _pending.Count;
    }

    /// <summary>
    /// Forgets every prediction (a respawn, a new connection or player). Not on the own death: the reports of hits landed just before it are still on their way and must
    /// find their predictions.
    /// </summary>
    public void Clear() => _pending.Clear();
}
