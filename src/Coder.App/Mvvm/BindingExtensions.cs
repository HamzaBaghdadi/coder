using System.ComponentModel;
using System.Linq.Expressions;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Coder.App.Mvvm;

/// <summary>
/// Fluent, strongly-typed bindings for code-only views:
/// <c>new TextBlock().Bind(TextBlock.TextProperty, vm, x =&gt; x.Title)</c>.
/// Uses compiled bindings (no reflection), so renames are compile errors and trimming stays safe.
/// </summary>
public static class BindingExtensions
{
    public static TControl Bind<TControl, TSource, TValue>(
        this TControl control,
        AvaloniaProperty property,
        TSource source,
        Expression<Func<TSource, TValue>> path,
        BindingMode mode = BindingMode.OneWay,
        IValueConverter? converter = null)
        where TControl : AvaloniaObject
    {
        control.Bind(property, CompiledBinding.Create(path, source: source, mode: mode, converter: converter));
        return control;
    }

    /// <summary>Binds through a plain conversion function.</summary>
    public static TControl Bind<TControl, TSource, TValue, TTarget>(
        this TControl control,
        AvaloniaProperty property,
        TSource source,
        Expression<Func<TSource, TValue>> path,
        Func<TValue, TTarget> convert)
        where TControl : AvaloniaObject =>
        control.Bind(property, source, path, BindingMode.OneWay, new FuncValueConverter<TValue, TTarget>(value => convert(value!)));

    /// <summary>Keeps a style class in sync with a condition on an observable source.</summary>
    public static TControl BindClass<TControl, TSource>(this TControl control, string className, TSource source, Func<TSource, bool> condition)
        where TControl : StyledElement
        where TSource : INotifyPropertyChanged
    {
        void Update() => control.Classes.Set(className, condition(source));
        Update();
        source.PropertyChanged += (_, _) => Update();
        return control;
    }
}
