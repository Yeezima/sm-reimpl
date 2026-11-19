using SmClient.Customizer;
namespace SmClient.FrontEnd;

/// <summary>
/// The front end's hook for added content (docs/added-content.md): the one implementation of <see cref="ExtrasMenuRows"/>, a dispatcher. It adds the rows of added content to the main menu,
/// under MUSOU MODE; today Warfront's local campaign (client/Extras/Warfront/FrontEnd/), which is compiled out with <c>-p:SmWarfront=false</c> (the partial then has no implementation and the call goes).
/// </summary>
public sealed partial class FrontEndView
{
    partial void ExtrasMenuRows(UiKit.MenuPage main) => WarfrontMenuRows(main);   // WARFRONT-HOOK

    /// <summary>Implemented in <c>client/Extras/Warfront/FrontEnd/FrontEndView.WarfrontMenu.cs</c>.</summary>
    partial void WarfrontMenuRows(UiKit.MenuPage main);
}
