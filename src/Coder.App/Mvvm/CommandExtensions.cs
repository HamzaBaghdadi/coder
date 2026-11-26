using System.Windows.Input;
using Coder.Presentation.Commands;
using Coder.Presentation.Mvvm;

namespace Coder.App.Mvvm;

public static class CommandExtensions
{
    /// <summary>Wraps a registered command for use as a button's <see cref="ICommand"/>.</summary>
    public static ICommand ToCommand(this CommandRegistry registry, string id) =>
        new RelayCommand(() => _ = registry.ExecuteAsync(id));
}
