namespace AcerHelper.Infrastructure.Vendors.Generic;

/// <summary>Reads the machine's manufacturer and product name (for vendor/model detection).
/// The source is OS-specific — WMI on Windows, DMI sysfs on Linux — supplied by the partial
/// <see cref="Read"/> in the matching MachineInfo.*.cs file.
///
/// <see cref="Read"/> KEEPS ITS TWO-FIELD SHAPE and <see cref="ReadIdentity"/> adds the two board fields on top
/// of it, because the plugin machine descriptor (§3.2 of docs/vendor-plugins.md) needs all four cheap DMI reads
/// while existing callers (the generic Linux backend, the old factory) only ever asked for the pair. Splitting
/// the pair out is what lets the board read be one extra query without touching every caller.</summary>
public static partial class MachineInfo
{
    public static partial (string? Manufacturer, string? Product) Read();

    /// <summary>The §3.2 four-field machine descriptor: the manufacturer/product pair above plus the
    /// <c>Win32_BaseBoard</c>/<c>board_*</c> fields the plugin's <c>ah_matches</c> uses to tell a board/OEM
    /// rebrand from the retail line. All four are one-line DMI reads (no hardware transaction), so the loader can
    /// build the descriptor once per scan. A null field is "not exposed", exactly as the pair's nulls are.</summary>
    internal static MachineIdentity ReadIdentity()
    {
        var (manufacturer, product) = Read();
        var (board, boardProduct) = ReadBoard();
        return new MachineIdentity(manufacturer, product, board, boardProduct);
    }

    /// <summary>The board fields of the descriptor. OS-specific like <see cref="Read"/>; a machine that exposes
    /// no baseboard (a VM, a desktop board) returns a pair of nulls rather than throwing.</summary>
    internal static partial (string? Board, string? BoardProduct) ReadBoard();
}

/// <summary>The §3.2 descriptor fields as one value, so the factory hands the loader four fields instead of
/// threading a tuple through. The field names mirror the wire names the descriptor JSON uses.</summary>
internal readonly record struct MachineIdentity(
    string? Manufacturer, string? Product, string? Board, string? BoardProduct);
