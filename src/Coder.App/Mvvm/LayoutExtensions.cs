using Avalonia.Controls;

namespace Coder.App.Mvvm;

/// <summary>Fluent helpers for attached layout properties, keeping code-only views declarative.</summary>
public static class LayoutExtensions
{
    public static T Row<T>(this T control, int row)
        where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }

    public static T Column<T>(this T control, int column)
        where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }

    public static T ColumnSpan<T>(this T control, int span)
        where T : Control
    {
        Grid.SetColumnSpan(control, span);
        return control;
    }

    public static T Dock<T>(this T control, Avalonia.Controls.Dock dock)
        where T : Control
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}
