using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmSim.Extras.Musou.Campaign
{
    /// <summary>
    /// INTEGRATION (TF5): the members the run shares with the AI Marines (branch <c>musou/tf5-marines</c>, <c>MarineBrain.cs</c>), same names and signatures. Delete this whole file when TF5
    /// lands, and put <c>World.TfwRetainerUp</c> in <c>World.Tfw.cs</c> with its real body (that file says how); until then no Marine exists and a retainer is never up.
    /// </summary>
    public sealed partial class TfwRun
    {
        public Vector3? ActivePosition { get; set; }
        public float WarTimeScale { get; set; } = 1f;
        public Func<int, bool>? ReviveWindow { get; set; }
        public Action<TfwLane>? LordFallen { get; set; }
        public bool ReviveWanted(int player) => ReviveWindow?.Invoke(player) ?? false;
    }
}

namespace SmSim
{
    public sealed partial class World
    {
        public int? TfwSpawnRetainer(int owner, Vector3 at, float yaw = 0f) => null;
        public int? TfwSpawnLord(TfwLane lane, Vector3 at, float yaw = 0f) => null;
        public void TfwDismissMarines() { }
        public void TfwResetMarines() { }
        public bool TfwOrderMarines(int player, TfwCommandRecord command) => false;
        void TfwPuppetsStep() { }
        bool TfwRetainerUp(int id) => false;

        /// <summary>TF5's revive: back where it fell with half its health and shield, keeping what it carries.</summary>
        public bool TfwRevive(int id)
        {
            if (_tfw is null || !_players.TryGetValue(id, out var p) || p.Alive) return false;
            var body = Loadouts.Body(Config, p);
            _players[id] = p with { Alive = true, Health = body.HealthMax * 0.5f, Shield = body.ShieldMax * 0.5f, RespawnLeft = 0f, SinceDamage = 0f, Grounded = true };
            _musouArmed.Add(id);
            return true;
        }
    }
}
