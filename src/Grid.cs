using System;

namespace StickyInk;

/// <summary>
/// Geometry shared by every part of the app.
///
/// The whole virtual screen (all monitors) is treated as one bitmap in physical
/// pixels. "Overlay coordinates" are screen coordinates minus (Left, Top), so the
/// top-left pixel of the virtual screen is (0, 0).
///
/// For scroll detection the screen is cut into vertical BANDS of
/// <see cref="BandWidth"/> pixels. A CELL is one pixel row inside one band, i.e.
/// a 64x1 pixel strip. Per-cell data is stored in flat arrays indexed by
/// <see cref="Cell"/>.
/// </summary>
internal sealed class Grid
{
    public const int BandWidth = 64;

    public readonly int Left, Top, Width, Height;
    public readonly int Bands;
    public readonly int Cells;

    public Grid()
    {
        Left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
        Top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
        Width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
        Height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
        Bands = (Width + BandWidth - 1) / BandWidth;
        Cells = Bands * Height;
    }

    /// <summary>Index of a cell in the flat per-cell arrays.</summary>
    public int Cell(int band, int row) => band * Height + row;

    public int ClampBand(int band) => Math.Max(0, Math.Min(Bands - 1, band));
    public int ClampRow(int row) => Math.Max(0, Math.Min(Height - 1, row));
}

/// <summary>An inclusive rectangle of cells: bands Band0..Band1, rows Row0..Row1.</summary>
internal readonly struct CellRange
{
    public readonly int Band0, Band1, Row0, Row1;

    public CellRange(int band0, int band1, int row0, int row1)
    {
        Band0 = band0; Band1 = band1; Row0 = row0; Row1 = row1;
    }

    public bool IsEmpty => Band0 > Band1 || Row0 > Row1;

    public static CellRange FullScreen(Grid grid) => new CellRange(0, grid.Bands - 1, 0, grid.Height - 1);
}

/// <summary>Bits stored per cell by the scroll tracker. A cell with flags == 0 is usable.</summary>
internal static class CellFlags
{
    /// <summary>Every pixel in the cell has the same color (blank background): carries no information.</summary>
    public const byte Flat = 1;

    /// <summary>Our own ink is (or just was) drawn over this cell: it must not be used for measuring.</summary>
    public const byte Inked = 2;
}
