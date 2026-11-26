using Coder.Core.Lsp;
using Coder.Core.Text;

namespace Coder.Presentation.Editors;

/// <summary>A diagnostic placed in a document as offsets, so it can follow edits until the server sends fresh ones.</summary>
public readonly record struct DiagnosticMarker(int Start, int End, Diagnostic Diagnostic)
{
    public DiagnosticSeverity Severity => Diagnostic.Severity;

    public static DiagnosticMarker From(Diagnostic diagnostic, TextDocument document)
    {
        var start = document.GetOffset(diagnostic.Range.Start);
        var end = Math.Max(start, document.GetOffset(diagnostic.Range.End));
        return new DiagnosticMarker(start, end, diagnostic);
    }

    public DiagnosticMarker Map(TextChange change) => this with { Start = change.MapOffset(Start), End = change.MapOffset(End) };

    /// <summary>True when <paramref name="offset"/> is on the marker (an empty marker counts at its position).</summary>
    public bool Contains(int offset) => offset >= Start && (offset < End || (Start == End && offset == Start));
}
