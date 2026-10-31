using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The enemies' separation hash (design section B: 1 m cells): a dense grid over the nav box, rebuilt each tick by a counting sort of the bodies' feet (points
/// outside the box go to its edge cells), so a neighbour search reads the 3 x 3 cells around a point. Items keep their insertion order within a cell
/// (deterministic).
/// </summary>
public sealed class SpatialGrid
{
    readonly float _x0, _y0, _cell;
    readonly int _w, _h;
    readonly int[] _cellStart, _fill;
    int[] _cellOf = [], _items = [];

    public SpatialGrid(MusouBox box, float cell)
    {
        _cell = MathF.Max(0.25f, cell);
        _x0 = box.Min.X; _y0 = box.Min.Y;
        _w = Math.Clamp((int)MathF.Ceiling((box.Max.X - box.Min.X) / _cell) + 1, 1, 4096);
        _h = Math.Clamp((int)MathF.Ceiling((box.Max.Y - box.Min.Y) / _cell) + 1, 1, 4096);
        _cellStart = new int[_w * _h + 1];
        _fill = new int[_w * _h];
    }

    /// <summary>Items in the grid.</summary>
    public int Count { get; private set; }

    int CellX(float x) => Math.Clamp((int)MathF.Floor((x - _x0) / _cell), 0, _w - 1);
    int CellY(float y) => Math.Clamp((int)MathF.Floor((y - _y0) / _cell), 0, _h - 1);

    /// <summary>Rebuilds the grid from <paramref name="count"/> items: item k at <paramref name="positions"/>[k], carrying the value <paramref name="values"/>[k].</summary>
    public void Build(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> values, int count)
    {
        if (_cellOf.Length < count) { _cellOf = new int[Math.Max(count, 64)]; _items = new int[Math.Max(count, 64)]; }
        Array.Clear(_cellStart);
        for (int k = 0; k < count; k++)
        {
            int c = CellX(positions[k].X) + CellY(positions[k].Y) * _w;
            _cellOf[k] = c;
            _cellStart[c + 1]++;
        }
        for (int c = 0; c < _w * _h; c++) { _cellStart[c + 1] += _cellStart[c]; _fill[c] = _cellStart[c]; }
        for (int k = 0; k < count; k++) _items[_fill[_cellOf[k]]++] = values[k];
        Count = count;
    }

    /// <summary>The values in the 3 x 3 cells around <paramref name="p"/>, written into <paramref name="into"/> as far as it holds them; the number written.</summary>
    public int Around(Vector3 p, Span<int> into)
    {
        int cx = CellX(p.X), cy = CellY(p.Y), n = 0;
        for (int y = Math.Max(0, cy - 1); y <= Math.Min(_h - 1, cy + 1); y++)
            for (int x = Math.Max(0, cx - 1); x <= Math.Min(_w - 1, cx + 1); x++)
            {
                int c = x + y * _w;
                for (int k = _cellStart[c]; k < _cellStart[c + 1]; k++)
                {
                    if (n >= into.Length) return n;
                    into[n++] = _items[k];
                }
            }
        return n;
    }
}
