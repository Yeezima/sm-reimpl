using SmSim;

namespace SmClient.Customizer;

/// <summary>
/// The customizer services' added-content dispatcher (docs/added-content.md: a partial method has one implementation, so a second feature's makes it a dispatcher): the one implementation of
/// <see cref="ExtrasConfig"/> and <see cref="ExtrasLoaded"/>. Musou's page plans its Joins with Musou's own configuration (<c>Musou/Arsenal/CustomizerServices.Arsenal.cs</c>); a Classic page with
/// <see cref="ContentExtras.Autocannon"/> with the data's configuration plus Classic PvP's autocannon (<c>Autocannon/CustomizerServices.ClassicAutocannon.cs</c>); every other page with the data's.
/// </summary>
public sealed partial class CustomizerServices
{
    partial void ExtrasConfig(ref SimConfig? config)
    {
        if (MusouExtrasConfig(ref config)) return;
        ClassicAutocannonConfig(ref config);
    }

    partial void ExtrasLoaded()
    {
        MusouExtrasLoaded();
        ClassicAutocannonLoaded();
    }
}
