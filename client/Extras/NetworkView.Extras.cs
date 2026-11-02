using System.Collections.Generic;
using SmNet;
using SmNet.Extras.LoyalistBarks;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmSim;

namespace SmClient;

/// <summary>
/// The networked view's frame hook for added content (docs/added-content.md): the one implementation of <see cref="ExtrasFrame"/>, a dispatcher. Once a frame it
/// drains the envelope queue (<c>GameClient.TakeExtras</c>) exactly once and hands each section to the feature that owns its tag (the loyalist barks: 2;
/// Musou: 3 to 6; Musou Mode: 7 to 15); a section of a tag nobody here owns (gore's 1, a newer build's) is counted and skipped. Then each feature's frame runs, in a fixed
/// order; each returns at once unless its flag is set in the match's config.
/// </summary>
public partial class NetworkView
{
    readonly List<ExtrasSection> _barkSections = new(), _musouSections = new(), _tfwSections = new();

    /// <summary>Envelope sections of a tag no feature of this build owns (skipped).</summary>
    public int ExtrasUnknownSections { get; private set; }

    partial void ExtrasFrame(PlayerState me, IReadOnlyList<PlayerState> others, double delta)
    {
        _barkSections.Clear();
        _musouSections.Clear();
        _tfwSections.Clear();
        if (_client is { Status: ClientStatus.Connected } client)
            foreach (var section in client.TakeExtras())
            {
                if (section.Tag == LoyalistShoutWire.Tag) _barkSections.Add(section);
                else if (MusouWire.Owns(section.Tag)) _musouSections.Add(section);
                else if (TfwWire.Owns(section.Tag)) _tfwSections.Add(section);
                else ExtrasUnknownSections++;
            }
        LoyalistBarksFrame(me, others, delta, _barkSections);
        MusouFrame(me, others, delta, _musouSections);
        TfwFrame(me, others, delta, _tfwSections);
    }

    /// <summary>A map added content composes: Musou Mode's.</summary>
    partial void ExtrasMeleeTarget(PlayerState me, MeleeStep step, ref System.Numerics.Vector3? at) => MusouMeleeTarget(me, step, ref at);

    partial void ExtrasMapFile(ref string file) => TfwMapFile(ref file);

    partial void ExtrasMapCollision(string name, ref SmSim.CollisionWorld world, ref ulong hash) => TfwMapCollision(name, ref world, ref hash);
}
