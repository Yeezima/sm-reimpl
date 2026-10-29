namespace SmFormats.Audio;

/// <summary>
/// Wwise short IDs: 32-bit FNV-1 (multiply, then xor) over the lower-cased name's bytes.
/// Event, bank, switch-group and switch-value names all hash this way (docs/discovery.md, "Audio (Wwise)").
/// </summary>
public static class Fnv
{
    const uint OffsetBasis = 2166136261;
    const uint Prime = 16777619;

    public static uint Hash32(string name)
    {
        uint h = OffsetBasis;
        foreach (char c in name)
        {
            // Wwise lower-cases ASCII only; names in this game are plain ASCII.
            char l = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            if (l < 0x80) h = unchecked(h * Prime) ^ l;
            else foreach (byte b in System.Text.Encoding.UTF8.GetBytes(l.ToString())) h = unchecked(h * Prime) ^ b;
        }
        return h;
    }
}
