using DeskPair.Platform.Abstractions.Capture;

namespace DeskPair.Core.Video;

/// <summary>
/// Tracks which tiles of a display changed since the last frame and which tiles the viewer holds in
/// lossless form. Tile hashes (<see cref="TileHash"/>) are recomputed for every tile unless the capturer reports dirty
/// rectangles, in which case only intersecting tiles are rehashed.
/// </summary>
public sealed class ChangeDetector
{
    private readonly int _tileSize;
    private ulong[] _hashes = [];
    private ulong[] _cleanHashes = [];
    private bool[] _needsRefine = [];
    private byte[] _activity = [];
    private int _width;
    private int _height;
    private bool _first = true;

    public ChangeDetector(int tileSize = TileCodec.DefaultTileSize)
    {
        _tileSize = tileSize;
    }

    public int TileSize => _tileSize;

    public int Columns { get; private set; }

    public int Rows { get; private set; }

    public int TileCount => Columns * Rows;

    /// <summary>Activity a tile must reach to count as motion: changed in roughly two consecutive frames.</summary>
    public const int MotionThreshold = 6;

    private const int ActivityGain = 3;
    private const int ActivityCap = 30;

    /// <summary>Tiles whose last delivery to the viewer was lossy (or never happened).</summary>
    public int PendingRefinement { get; private set; }

    /// <summary>Tiles currently in motion (video playback, dragging, animation); they belong to the video path.</summary>
    public int MotionTileCount { get; private set; }

    public bool NeedsRefinement(int tile) => _needsRefine[tile];

    /// <summary>True when the tile changed in recent consecutive frames, i.e. it is video-like content rather than a one-off edit.</summary>
    public bool IsMotion(int tile) => _activity[tile] >= MotionThreshold;

    public bool AnyMotion(List<int> tiles)
    {
        foreach (int t in tiles)
        {
            if (IsMotion(t))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A capture tick with no new frame: activity decays so motion regions settle after they stop.</summary>
    public void Idle() => Decay(null);

    /// <summary>Rehashes the frame and returns the tiles that changed since the previous call (all tiles on the first frame or a size change).</summary>
    public List<int> Update(ReadOnlySpan<byte> bgra, int stride, int width, int height, ReadOnlySpan<PixelRect> dirtyRects = default)
    {
        if (width != _width || height != _height)
        {
            Resize(width, height);
        }

        var changed = new List<int>();
        if (_first || dirtyRects.IsEmpty)
        {
            for (int row = 0; row < Rows; row++)
            {
                for (int col = 0; col < Columns; col++)
                {
                    Check(bgra, stride, col, row, changed);
                }
            }
        }
        else
        {
            var seen = new HashSet<int>();
            foreach (PixelRect rect in dirtyRects)
            {
                int c0 = Math.Clamp(rect.X / _tileSize, 0, Columns - 1);
                int c1 = Math.Clamp((rect.X + rect.Width - 1) / _tileSize, 0, Columns - 1);
                int r0 = Math.Clamp(rect.Y / _tileSize, 0, Rows - 1);
                int r1 = Math.Clamp((rect.Y + rect.Height - 1) / _tileSize, 0, Rows - 1);
                for (int row = r0; row <= r1; row++)
                {
                    for (int col = c0; col <= c1; col++)
                    {
                        if (seen.Add(row * Columns + col))
                        {
                            Check(bgra, stride, col, row, changed);
                        }
                    }
                }
            }
        }

        _first = false;
        Decay(changed);
        return changed;
    }

    private void Decay(List<int>? changed)
    {
        int motion = 0;
        for (int t = 0; t < _activity.Length; t++)
        {
            if (_activity[t] > 0)
            {
                _activity[t]--;
            }
        }

        if (changed is not null)
        {
            foreach (int t in changed)
            {
                _activity[t] = (byte)Math.Min(ActivityCap, _activity[t] + ActivityGain + 1);
            }
        }

        for (int t = 0; t < _activity.Length; t++)
        {
            if (_activity[t] >= MotionThreshold)
            {
                motion++;
            }
        }

        MotionTileCount = motion;
    }

    /// <summary>The viewer now holds these tiles losslessly at their current content.</summary>
    public void MarkClean(IEnumerable<int> tiles)
    {
        foreach (int t in tiles)
        {
            if (_needsRefine[t])
            {
                _needsRefine[t] = false;
                PendingRefinement--;
            }

            _cleanHashes[t] = _hashes[t];
        }
    }

    /// <summary>The viewer received these tiles through a lossy path; they need refinement once the screen settles.</summary>
    public void MarkLossy(IEnumerable<int> tiles)
    {
        foreach (int t in tiles)
        {
            if (!_needsRefine[t])
            {
                _needsRefine[t] = true;
                PendingRefinement++;
            }
        }
    }

    /// <summary>Every tile is lossy (a full video frame was sent).</summary>
    public void MarkAllLossy()
    {
        Array.Fill(_needsRefine, true);
        PendingRefinement = TileCount;
    }

    /// <summary>Tiles still waiting for a lossless copy, nearest to <paramref name="focusX"/>,<paramref name="focusY"/> first.</summary>
    public List<int> RefinementCandidates(int focusX, int focusY, int max)
    {
        var list = new List<int>();
        for (int t = 0; t < _needsRefine.Length; t++)
        {
            if (_needsRefine[t] && !IsMotion(t))
            {
                list.Add(t); // a tile still moving would be refined for nothing
            }
        }

        // Whole rows, spreading up and down from the row under the focus, left to right inside a row: the picture
        // sharpens in coherent bands instead of scattered squares.
        int fr = Math.Clamp(focusY / _tileSize, 0, Math.Max(0, Rows - 1));
        list.Sort((a, b) =>
        {
            int ra = a / Columns, rb = b / Columns;
            int da = Math.Abs(ra - fr), db = Math.Abs(rb - fr);
            if (da != db)
            {
                return da.CompareTo(db);
            }

            return ra != rb ? ra.CompareTo(rb) : a.CompareTo(b);
        });
        if (list.Count > max)
        {
            list.RemoveRange(max, list.Count - max);
        }

        return list;
    }

    public (int X, int Y, int Width, int Height) TileRect(int tile)
    {
        int col = tile % Columns, row = tile / Columns;
        int x = col * _tileSize, y = row * _tileSize;
        return (x, y, Math.Min(_tileSize, _width - x), Math.Min(_tileSize, _height - y));
    }

    private void Check(ReadOnlySpan<byte> bgra, int stride, int col, int row, List<int> changed)
    {
        int tile = row * Columns + col;
        ulong hash = Hash(bgra, stride, col, row);
        if (_first || hash != _hashes[tile])
        {
            _hashes[tile] = hash;
            changed.Add(tile);
        }
    }

    private ulong Hash(ReadOnlySpan<byte> bgra, int stride, int col, int row)
    {
        (int x, int y, int w, int h) = TileRect(row * Columns + col);
        return TileHash.Compute(bgra[(y * stride + x * 4)..], stride, w, h);
    }

    private void Resize(int width, int height)
    {
        _width = width;
        _height = height;
        Columns = (width + _tileSize - 1) / _tileSize;
        Rows = (height + _tileSize - 1) / _tileSize;
        _hashes = new ulong[TileCount];
        _cleanHashes = new ulong[TileCount];
        _needsRefine = new bool[TileCount];
        _activity = new byte[TileCount];
        PendingRefinement = 0;
        _first = true;
    }
}
