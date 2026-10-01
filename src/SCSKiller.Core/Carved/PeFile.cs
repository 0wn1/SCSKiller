using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace SCSKiller.Core.Carved;

/// <summary>A PE file's export directory (IMAGE_EXPORT_DIRECTORY: 40 bytes, Name RVA at 12, NumberOfNames at 24,
/// AddressOfNames at 32).</summary>
public static class PeFile
{
    /// <summary>Read-only, while a game has the file loaded or a launcher replaces it.</summary>
    public static PEReader Open(string path) => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

    /// <summary>What the DLL was linked as (kept when the file is renamed); null if none.</summary>
    public static string? ExportName(PEReader pe)
    {
        if (ExportDirectory(pe) is not { } d) return null;
        d.Offset = 12;
        return Str(pe, d.ReadInt32()) is { Length: > 0 } name ? name : null;
    }

    /// <summary>The exported names, in table order.</summary>
    public static IEnumerable<string> ExportNames(PEReader pe)
    {
        if (ExportDirectory(pe) is not { } d) yield break;
        d.Offset = 24;
        var count = d.ReadInt32();
        d.Offset = 32;
        var names = Data(pe, d.ReadInt32());
        for (var i = 0; i < count && names.RemainingBytes >= 4; i++) yield return Str(pe, names.ReadInt32());
    }

    /// <summary>The NUL-terminated string at an RVA, at most 256 characters.</summary>
    internal static string Str(PEReader pe, int rva)
    {
        var r = Data(pe, rva);
        var sb = new StringBuilder();
        for (byte b; r.RemainingBytes > 0 && sb.Length < 256 && (b = r.ReadByte()) != 0;) sb.Append((char)b);
        return sb.ToString();
    }

    static BlobReader? ExportDirectory(PEReader pe) =>
        Data(pe, pe.PEHeaders.PEHeader?.ExportTableDirectory.RelativeVirtualAddress ?? 0) is { RemainingBytes: >= 40 } d ? d : null;

    static BlobReader Data(PEReader pe, int rva) => rva <= 0 ? default : pe.GetSectionData(rva).GetReader();
}
