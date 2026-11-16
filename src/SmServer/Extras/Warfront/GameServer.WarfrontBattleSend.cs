using SmNet;
using SmNet.Extras.Warfront;

namespace SmServer;

/// <summary>The Battle section (slice W13b, tag 47; <see cref="WarfrontBattleWire"/>): to every identified client while a Warfront battle runs, on a change and once a second. W5a's <c>WarfrontSend</c> calls <c>WarfrontSendBattle()</c> (routed to the lead).</summary>
public sealed partial class GameServer
{
    byte[]? _wfBattleLast;

    partial void WarfrontSendBattle();

    partial void WarfrontSendBattle()
    {
        if (_wf is not { } s) return;
        if (World.Tfw is not { Battle: { } battle } run || run.Warfront is not { InBattle: true } rules || s.Campaign.CurrentTarget is not { } target)
        {
            _wfBattleLast = null;
            return;
        }
        byte[] bytes = WarfrontBattleWire.Encode(WarfrontBattleWire.FromBattle(battle, target.Kind));
        bool changed = _wfBattleLast is null || !_wfBattleLast.AsSpan().SequenceEqual(bytes);
        _wfBattleLast = bytes;
        if (!changed && World.Tick % (uint)WarfrontRate != 0) return;
        foreach (int id in s.KeyOf.Keys.ToList())
            SendExtras(new ExtrasUpdate(World.Tick, [(WarfrontBattleWire.EnvelopeTag, bytes)]), id);
    }
}
