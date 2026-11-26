using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Coder.App.Controls;

public static class ListBoxExtensions
{
    /// <summary>
    /// Handles keys pressed on a list's rows (Enter to open, arrows to expand…). The handler runs before
    /// the list's own key handling, which would otherwise consume Enter; keys pressed on a button inside
    /// a row stay with that button.
    /// </summary>
    public static void OnRowKeyDown(this ListBox list, EventHandler<KeyEventArgs> handler) =>
        list.AddHandler(InputElement.KeyDownEvent, (sender, e) =>
        {
            if (e.Source is ListBoxItem)
            {
                handler(sender, e);
            }
        }, RoutingStrategies.Tunnel);
}
