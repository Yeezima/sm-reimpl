namespace SmContent;

/// <summary>
/// musou/hotkey-lines (owner, 2026-11-10, Musou Mode only): "remove the voice barks feature we added previously. we'll stick to the hotkey and simply add more lines for it to say based on what i mark
/// to keep in the interactive sheet you gave me earlier"; with the six lines he kept: "use these voice lines in rotation with 'for the emperor' when pressing T on the keyboard."
/// <list type="bullet">
/// <item>The T key (the loyalist barks' FOR THE EMPEROR key, <c>LoyalistShoutWire</c>: the same cooldown, the same server relay, the same shout sequence) says one line of <see cref="Pool"/> per
/// accepted press, in the pool's order, slot 0 first: FOR THE EMPEROR (the voice pack's takes, as before), then the six campaign lines the owner kept in the listening sheet
/// (<c>D:\sm-reimpl\media\previews\musou-generic-barks\listen\checklist.md</c>, rows 02 to 06 and 08). The slot is a function of the player's shout sequence alone (<see cref="SlotOf"/>), so every
/// client hears the same line for a press, as it hears the same take today.</item>
/// <item>The six lines are the install's own audio (dialogue leaves of the speech bank <c>speech_01</c>, posted by node id; a singleplayer-content dependency: the campaign squad's lines,
/// docs/discovery.md); nothing of them is in the repository.</item>
/// <item>The automatic Marine barks of musou/battle-ambience (the random campaign lines every few seconds) are off; <see cref="AutoBarks"/> brings them back with <c>--musou-auto-barks</c>.</item>
/// </list>
/// Classic PvP (Annihilation, Seize Ground) keeps the four FOR THE EMPEROR takes on T: <see cref="Applies"/> is the one gate.
/// </summary>
public static class MusouKeyLines
{
    /// <summary>
    /// One line of the key's rotation: the speech bank's dialogue leaf (0 = FOR THE EMPEROR, the voice pack's take), its speaker, the sheet's transcript, the sheet's row, the leaf's wem and its level
    /// offset (dB over the data's level, **ours**: <see cref="Pool"/>).
    /// </summary>
    public readonly record struct KeyLine(uint Node, string Character, string Text, int SheetRow, uint Wem, float GainDb = 0f)
    {
        /// <summary>Slot 0: the voice pack's FOR THE EMPEROR take (not a speech-bank line).</summary>
        public bool Emperor => Node == 0;
    }

    /// <summary>
    /// The rotation, in order (the owner's six kept lines in the sheet's order after FOR THE EMPEROR). Transcripts are the sheet's (an offline recognizer's, checked by the owner's ear).
    /// Levels (**ours**, to refine by ear): at the data's level the six came out 11 to 18 dB under the take in the first capture (2026-11-10: active level -26 to -34 dB against -15.5 dB; a
    /// battle's bed is about -19 dB, so they were lost in a fight). Each line's offset brings its decoded file's active level (-24.9, -23.2, -20.5, -19.3, -18.1, -15.0 dB) to the loudest's
    /// (Sidonus's shout), plus the 12 dB that puts that one level with the take in the capture, capped so that no line's loudest 20 ms passes the take's (-11.4 dB in the capture).
    /// <c>--musou-key-gain &lt;dB&gt;</c> adds to all six; <c>--musou-key-data-level</c> plays them at the data's level.
    /// </summary>
    public static readonly IReadOnlyList<KeyLine> Pool =
    [
        new(0u, "pack", "FOR THE EMPEROR!", 0, 0u),
        new(0x0616864eu, "titus", "Purge the xenos!", 2, 202735440u, 20f),
        new(0x0837844fu, "titus", "Send the Horde, brother.", 3, 202735438u, 19f),
        new(0x0cda2bd9u, "titus", "Excellently done.", 4, 202735430u, 13f),
        new(0x1ae56979u, "titus", "One less traitor.", 5, 202735428u, 14f),
        new(0x1f2fe75du, "titus", "OFF, YOU ALIEN PEST!", 6, 202735475u, 15f),
        new(0x1c832a04u, "sidonus", "AHHH!", 8, 202735038u, 12f),
    ];

    /// <summary>The gate: Musou Mode only (the Musou content flag), as <see cref="MusouAudioRules.Applies"/>.</summary>
    public static bool Applies(SmSim.ContentExtras extras) => MusouAudioRules.Applies(extras);

    /// <summary>
    /// The pool slot of a player's shout <paramref name="sequence"/> (the server's per-player count of accepted presses: 1 for the first, wrapping 255 to 0): the pool in order, slot 0 first, each
    /// slot once before any repeats. Exception, once per 256 presses of a player (the u8 sequence's wrap, 256 not being a multiple of 7): sequences 253, 254, 255, 0 give slots 0 to 3 and the next
    /// press (sequence 1) starts the pool again at slot 0, so slots 4 to 6 are skipped that once. The wire stays as it is (owner's brief: the key's networking unchanged).
    /// </summary>
    public static int SlotOf(byte sequence) => (byte)(sequence - 1) % Pool.Count;

    /// <summary>The line of a shout sequence (<see cref="SlotOf"/>).</summary>
    public static KeyLine LineOf(byte sequence) => Pool[SlotOf(sequence)];

    /// <summary>
    /// Whether musou/battle-ambience's automatic Marine barks run (the own player as Titus, the others as Sidonus or Leandros, every few seconds): only with the restore switch
    /// <c>--musou-auto-barks</c>, and never with <c>--no-musou-marine-barks</c>. <paramref name="args"/>: the client's user arguments.
    /// </summary>
    public static bool AutoBarks(IReadOnlyCollection<string> args) => args.Contains(AutoBarksFlag) && !args.Contains("--no-musou-marine-barks");

    /// <summary>The restore switch of the automatic Marine barks.</summary>
    public const string AutoBarksFlag = "--musou-auto-barks";

    /// <summary>The six lines at the data's level (no offsets of <see cref="Pool"/>).</summary>
    public const string DataLevelFlag = "--musou-key-data-level";

    /// <summary>The restore switch of the key: T says FOR THE EMPEROR only, as before (the four takes in turn).</summary>
    public const string EmperorOnlyFlag = "--no-musou-key-lines";
}
