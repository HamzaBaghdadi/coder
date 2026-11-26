using System.Collections.ObjectModel;
using Coder.Core.Lsp;
using Coder.Presentation.Mvvm;

namespace Coder.Presentation.LanguageFeatures;

/// <summary>A row of the Problems panel: a file with its count, or one problem in it.</summary>
public abstract record ProblemRow(string Path);

public sealed record ProblemFileRow(string Path, string Folder, int Count) : ProblemRow(Path)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

public sealed record ProblemItemRow(string Path, Diagnostic Diagnostic) : ProblemRow(Path)
{
    /// <summary>"[Ln 4, Col 2]", one-based like the status bar.</summary>
    public string Position => $"[Ln {Diagnostic.Range.Start.Line + 1}, Col {Diagnostic.Range.Start.Column + 1}]";
}

/// <summary>
/// Every problem the language servers reported, grouped by file: files with errors first, and inside
/// a file errors before warnings, then by position. Activating a problem opens the file there.
/// </summary>
public sealed class ProblemsViewModel : ObservableObject
{
    private readonly LanguageServices _languages;
    private readonly Func<string, TextRange, Task> _reveal;
    private readonly Func<string?> _workspaceFolder;

    public ProblemsViewModel(LanguageServices languages, Func<string?> workspaceFolder, Func<string, TextRange, Task> reveal)
    {
        _languages = languages;
        _workspaceFolder = workspaceFolder;
        _reveal = reveal;
        languages.DiagnosticsChanged += (_, _) => Rebuild();
    }

    public ObservableCollection<ProblemRow> Rows { get; } = [];

    public int Count => _languages.ErrorCount + _languages.WarningCount + InfoCount;

    public bool IsEmpty => Rows.Count == 0;

    private int InfoCount => Rows.OfType<ProblemItemRow>().Count(r => r.Diagnostic.Severity > DiagnosticSeverity.Warning);

    public Task ActivateAsync(ProblemRow row) =>
        row is ProblemItemRow item ? _reveal(item.Path, item.Diagnostic.Range) : Task.CompletedTask;

    private void Rebuild()
    {
        Rows.Clear();
        var root = _workspaceFolder();
        var files = _languages.AllDiagnostics
            .Where(pair => pair.Value.Count > 0)
            .OrderBy(pair => pair.Value.Min(d => d.Severity))
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase);
        foreach (var (path, diagnostics) in files)
        {
            var folder = Path.GetDirectoryName(path) ?? string.Empty;
            if (root is not null && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                folder = Path.GetRelativePath(root, folder) is "." ? string.Empty : Path.GetRelativePath(root, folder);
            }

            Rows.Add(new ProblemFileRow(path, folder, diagnostics.Count));
            foreach (var diagnostic in diagnostics.OrderBy(d => d.Severity).ThenBy(d => d.Range.Start))
            {
                Rows.Add(new ProblemItemRow(path, diagnostic));
            }
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
