using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Coder.App.Mvvm;

/// <summary>Data templates for code-only views.</summary>
public static class Templates
{
    /// <summary>
    /// A row template for list and items controls. Virtualizing lists recycle row containers by
    /// clearing their content to <c>null</c> first, and the template is asked to build for that null;
    /// it gets an empty placeholder instead of a row. Rows are built for one specific item (no
    /// recycling), so their bindings never point at the wrong data.
    /// </summary>
    public static FuncDataTemplate<T> Row<T>(Func<T, Control> build)
        where T : class =>
        new((item, _) => item is null ? new Panel() : build(item), supportsRecycling: false);
}
