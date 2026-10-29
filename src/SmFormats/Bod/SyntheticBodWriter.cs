using System.Text;

namespace SmFormats.Bod;

/// <summary>
/// Builds synthetic uncompressed BOD documents following our documented interpretation of the layout.
/// String hashes are FNV-1a 64 stand-ins; the game's hash function is unknown. For parser tests only.
/// </summary>
public static class SyntheticBodWriter
{
    /// A material like the real ".bmat" files: Name, ShaderName, and a Parameters list of TextureParameter structs.
    public static byte[] Material(string name, string shader, IReadOnlyList<(string Parameter, string Texture)> textures, float tileU = 1f, bool wargear = false, float[]? dirtColour = null)
    {
        var strings = new List<string>();
        ulong S(string s) { if (!strings.Contains(s)) strings.Add(s); return Hash(s); }

        var tree = new MemoryStream();
        var t = new BinaryWriter(tree);
        t.Write((byte)0x01); t.Write(S("Material")); t.Write(3);
        t.Write(S("Name")); t.Write((byte)0x0F); t.Write(S(name));
        t.Write(S("ShaderName")); t.Write((byte)0x0F); t.Write(S(shader));
        t.Write(S("Parameters")); t.Write((byte)0x09); t.Write((wargear ? 1 : textures.Count) + 1); t.Write((byte)1);
        if (wargear)
        {
            // The Chaos armour's shape: one WargearTextureParameter whose fields are the texture names.
            t.Write((byte)0x05); t.Write(S("DiffuseMap"));
            t.Write((byte)0x07); t.Write(0);
            t.Write((byte)0x01); t.Write(S("WargearTextureParameter")); t.Write(textures.Count + 1 + (dirtColour is null ? 0 : 1));
            foreach (var (parameter, texture) in textures) { t.Write(S(parameter)); t.Write((byte)0x0F); t.Write(S(texture)); }
            if (dirtColour is not null) { t.Write(S("DirtColour")); t.Write((byte)0x0B); t.Write(dirtColour.Length); foreach (var f in dirtColour) { t.Write((byte)0x03); t.Write(f); } }
            t.Write(S("Name")); t.Write((byte)0x0F); t.Write(S("DiffuseMap"));
        }
        for (int i = 0; i < (wargear ? 0 : textures.Count); i++)
        {
            t.Write((byte)0x05); t.Write(S(textures[i].Parameter));
            t.Write((byte)0x07); t.Write(i);
            t.Write((byte)0x01); t.Write(S("TextureParameter")); t.Write(3);
            t.Write(S("Value")); t.Write((byte)0x0F); t.Write(S(textures[i].Texture));
            t.Write(S("PackageName")); t.Write((byte)0x0F); t.Write(0UL);
            t.Write(S("Name")); t.Write((byte)0x0F); t.Write(S(textures[i].Parameter));
        }
        // One non-texture parameter, as in real files: a float and a 4-float array.
        t.Write((byte)0x05); t.Write(S("TileU"));
        t.Write((byte)0x07); t.Write(wargear ? 1 : textures.Count);
        t.Write((byte)0x01); t.Write(S("FloatParameter")); t.Write(3);
        t.Write(S("Value")); t.Write((byte)0x03); t.Write(tileU);
        t.Write(S("Extra")); t.Write((byte)0x0B); t.Write(4); for (int k = 0; k < 4; k++) { t.Write((byte)0x03); t.Write(1f); }
        t.Write(S("Name")); t.Write((byte)0x0F); t.Write(S("TileU"));
        t.Flush();

        var output = new MemoryStream();
        var o = new BinaryWriter(output, Encoding.ASCII);
        o.Write([0x42, 0x4F, 0x44, 0xFD]); o.Write((byte)2); o.Write((byte)0); o.Write(1u);
        o.Write(strings.Count); o.Write(strings.Count - 1);
        foreach (var s in strings)
        {
            var b = Encoding.ASCII.GetBytes(s);
            o.Write(Hash(s)); o.Write(b.Length); o.Write(b);
        }
        o.Write(tree.ToArray());
        return output.ToArray();
    }

    /// <summary>
    /// A level layer shaped like the game's <c>d_tdm.Layer</c>: a RegionLayer whose NavMesh (an empty NavMeshChunk with
    /// a 0x0D identifier) and Root (a WorldGroup) are 0x07 references, the Root holding a keyless list of marker
    /// spheres with Position, Rotation (degrees) and Scale vectors, a bool, a null value and an empty child list.
    /// </summary>
    public static byte[] Layer(IReadOnlyList<(string Name, float[] Position, float[] Rotation)> markers)
    {
        var strings = new List<string>();
        ulong S(string s) { if (!strings.Contains(s)) strings.Add(s); return Hash(s); }
        var tree = new MemoryStream();
        var t = new BinaryWriter(tree);
        void Vec(string name, float[] v) { t.Write(S(name)); t.Write((byte)0x0B); t.Write(v.Length); foreach (var x in v) { t.Write((byte)0x03); t.Write(x); } }
        void EmptyList(string name) { t.Write(S(name)); t.Write((byte)0x09); t.Write(0); t.Write((byte)0); }

        t.Write((byte)0x01); t.Write(S("RegionLayer")); t.Write(4);
        t.Write(S("ID")); t.Write((byte)0x02); t.Write(52);
        t.Write(S("IsNavMeshCover")); t.Write((byte)0x04); t.Write((byte)0);
        t.Write(S("NavMesh")); t.Write((byte)0x07); t.Write(0);
        t.Write((byte)0x01); t.Write(S("NavMeshChunk")); t.Write(2);
        t.Write(S("GUID")); t.Write((byte)0x0D); t.Write(0x43E8ECA5F7F9E2F2UL);
        EmptyList("Vertices");
        t.Write(S("Root")); t.Write((byte)0x07); t.Write(1);
        t.Write((byte)0x01); t.Write(S("WorldGroup")); t.Write(2);
        t.Write(S("Name")); t.Write((byte)0x0F); t.Write(0UL);
        t.Write(S("Objects")); t.Write((byte)0x09); t.Write(markers.Count); t.Write((byte)0);
        for (int i = 0; i < markers.Count; i++)
        {
            t.Write((byte)0x07); t.Write(2 + i);
            t.Write((byte)0x01); t.Write(S("base/missionmarkersphere")); t.Write(7);
            Vec("Position", markers[i].Position);
            Vec("Rotation", markers[i].Rotation);
            Vec("Scale", [1f, 1f, 5f]);
            t.Write(S("Name")); t.Write((byte)0x0F); t.Write(S(markers[i].Name));
            t.Write(S("Disabled")); t.Write((byte)0x04); t.Write((byte)0);
            t.Write(S("Unknown")); t.Write((byte)0x00);
            EmptyList("ChildObjects");
        }
        t.Flush();

        var output = new MemoryStream();
        var o = new BinaryWriter(output, Encoding.ASCII);
        o.Write([0x42, 0x4F, 0x44, 0xFD]); o.Write((byte)2); o.Write((byte)0); o.Write(1u);
        o.Write(strings.Count); o.Write(strings.Count - 1);
        foreach (var s in strings)
        {
            var b = Encoding.ASCII.GetBytes(s);
            o.Write(Hash(s)); o.Write(b.Length); o.Write(b);
        }
        o.Write(tree.ToArray());
        return output.ToArray();
    }

    /// <summary>A value for <see cref="Document"/>: struct, scalar, vector, reference, or list of references.</summary>
    public abstract record Node;
    public sealed record Struct(string Class, params (string Name, Node Value)[] Fields) : Node;
    public sealed record F(float Value) : Node;
    public sealed record I(int Value) : Node;
    public sealed record S(string Value) : Node;
    public sealed record B(bool Value) : Node;
    public sealed record Vec(params float[] Values) : Node;
    /// <summary>An eight-byte identifier (type 0x0D); the lighting layers store fog colour bytes this way.</summary>
    public sealed record U64(ulong Value) : Node;
    /// <summary>A 0x0B array of any typed values (nested arrays too; effect curve keys: [time, value] or [time, [r, g, b, a]]).</summary>
    public sealed record Arr(params Node[] Items) : Node;
    /// <summary>A 0x07 reference (position 0) to a value.</summary>
    public sealed record Ref(Node Value) : Node;
    /// <summary>A keyless list (flag 0) of 0x07 references, as layers and object definitions use.</summary>
    public sealed record List(params Node[] Items) : Node;
    /// <summary>A 0x0D eight-byte value (colour components in effect curves).</summary>
    public sealed record U(ulong Value) : Node;
    /// <summary>A keyless list (flag 0) of plain values, not references (effect curves).</summary>
    public sealed record Values(params Node[] Items) : Node;

    /// <summary>Any tree of <see cref="Node"/>s as an uncompressed BOD document.</summary>
    public static byte[] Document(Node root)
    {
        var strings = new List<string>();
        ulong Str(string s) { if (!strings.Contains(s)) strings.Add(s); return Hash(s); }
        var tree = new MemoryStream();
        var t = new BinaryWriter(tree);
        int position = 0;
        void Write(Node n)
        {
            switch (n)
            {
                case Struct s:
                    t.Write((byte)0x01); t.Write(Str(s.Class)); t.Write(s.Fields.Length);
                    foreach (var (name, value) in s.Fields) { t.Write(Str(name)); Write(value); }
                    break;
                case F f: t.Write((byte)0x03); t.Write(f.Value); break;
                case I i: t.Write((byte)0x02); t.Write(i.Value); break;
                case S str: t.Write((byte)0x0F); t.Write(str.Value.Length == 0 ? 0UL : Str(str.Value)); break;
                case B b: t.Write((byte)0x04); t.Write((byte)(b.Value ? 1 : 0)); break;
                case U64 u: t.Write((byte)0x0D); t.Write(u.Value); break;
                case Arr a: t.Write((byte)0x0B); t.Write(a.Items.Length); foreach (var item in a.Items) Write(item); break;
                case Vec v: t.Write((byte)0x0B); t.Write(v.Values.Length); foreach (var x in v.Values) { t.Write((byte)0x03); t.Write(x); } break;
                case Ref r: t.Write((byte)0x07); t.Write(position++); Write(r.Value); break;
                case List l:
                    t.Write((byte)0x09); t.Write(l.Items.Length); t.Write((byte)0);
                    foreach (var item in l.Items) { t.Write((byte)0x07); t.Write(position++); Write(item); }
                    break;
                case U u: t.Write((byte)0x0D); t.Write(u.Value); break;
                case Values vs: t.Write((byte)0x09); t.Write(vs.Items.Length); t.Write((byte)0); foreach (var item in vs.Items) Write(item); break;
                default: throw new ArgumentException($"Unsupported node {n}.");
            }
        }
        Write(root);
        t.Flush();
        var output = new MemoryStream();
        var o = new BinaryWriter(output, Encoding.ASCII);
        o.Write([0x42, 0x4F, 0x44, 0xFD]); o.Write((byte)2); o.Write((byte)0); o.Write(1u);
        o.Write(strings.Count); o.Write(Math.Max(0, strings.Count - 1));
        foreach (var s in strings)
        {
            var b = Encoding.ASCII.GetBytes(s);
            o.Write(Hash(s)); o.Write(b.Length); o.Write(b);
        }
        o.Write(tree.ToArray());
        return output.ToArray();
    }

    public static ulong Hash(string s)
    {
        ulong h = 14695981039346656037;
        foreach (byte b in Encoding.ASCII.GetBytes(s)) h = (h ^ b) * 1099511628211;
        return h;
    }
}
