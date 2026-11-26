using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Coder.Presentation.Workbench;

namespace Coder.App.Editor;

/// <summary>
/// Floats language features over the code editor: the completion list under the word being typed and
/// the hover card above the word under the pointer. It routes the keys the list needs (arrows, Enter,
/// Tab, Esc) while it is open, tells completion about typed text, and handles Ctrl+Click.
/// </summary>
public sealed class LanguageOverlay : Canvas
{
    private static readonly TimeSpan HoverGrace = TimeSpan.FromMilliseconds(250);

    private readonly CodeEditor _editor;
    private readonly WorkbenchViewModel _workbench;
    private readonly CompletionWidget _completion;
    private readonly HoverCard _hover = new();
    private readonly SignatureHelpCard _signature = new();
    private readonly DispatcherTimer _hideHover;
    private int _hoverGeneration;

    public LanguageOverlay(CodeEditor editor, WorkbenchViewModel workbench)
    {
        _editor = editor;
        _workbench = workbench;
        _completion = new CompletionWidget(workbench.Completion);
        ClipToBounds = true;
        Children.Add(_completion);
        Children.Add(_hover);
        Children.Add(_signature);

        _hideHover = new DispatcherTimer(HoverGrace, DispatcherPriority.Input, (_, _) =>
        {
            _hideHover!.Stop();
            if (!_hover.IsPointerOver)
            {
                HideHover();
            }
        });

        workbench.Completion.Changed += (_, _) => PlaceCompletion();
        workbench.SignatureHelp.Changed += (_, _) => PlaceSignatureHelp();
        editor.Scrolled += (_, _) =>
        {
            PlaceCompletion();
            PlaceSignatureHelp();
            HideHover();
        };
        editor.HoverRequested += async (_, offset) => await ShowHoverAsync(offset);
        editor.HoverEnded += (_, _) => _hideHover.Start();
        editor.DefinitionRequested += async (_, _) => await workbench.GoToDefinitionAsync();
        _hover.PointerExited += (_, _) => _hideHover.Start();

        editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        editor.AddHandler(TextInputEvent, (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Text) && !e.Text.Any(char.IsControl))
            {
                workbench.Completion.OnTextTyped(e.Text);
                workbench.SignatureHelp.OnTextTyped(e.Text);
            }
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        editor.AddHandler(PointerPressedEvent, (_, _) =>
        {
            workbench.Completion.Close();
            HideHover();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        editor.LostFocus += (_, _) =>
        {
            if (!editor.IsKeyboardFocusWithin)
            {
                workbench.Completion.Close();
            }
        };
    }

    /// <summary>While the list is open, navigation keys drive it instead of the caret.</summary>
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        var completion = _workbench.Completion;
        if (e.Key == Key.Escape && _hover.IsVisible)
        {
            HideHover();
        }

        // Esc closes the suggestion list first, then the parameter hint.
        if (e.Key == Key.Escape && !completion.IsOpen && _workbench.SignatureHelp.IsOpen)
        {
            _workbench.SignatureHelp.Close();
            e.Handled = true;
            return;
        }

        if (!completion.IsOpen || e.KeyModifiers != KeyModifiers.None)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                completion.MoveSelection(1);
                break;
            case Key.Up:
                completion.MoveSelection(-1);
                break;
            case Key.PageDown:
                completion.MoveSelection(CompletionWidget.VisibleRows - 1);
                break;
            case Key.PageUp:
                completion.MoveSelection(-(CompletionWidget.VisibleRows - 1));
                break;
            case Key.Enter or Key.Tab:
                completion.Accept();
                _workbench.SignatureHelp.Trigger(); // a function was likely inserted; the server closes the hint otherwise
                break;
            case Key.Escape:
                completion.Close();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>Under the word being completed, labels aligned with the typed text; above it when there is no room below.</summary>
    private void PlaceCompletion()
    {
        var completion = _workbench.Completion;
        if (!completion.IsOpen || _editor.CharacterBounds(completion.AnchorOffset, this) is not { } anchor
            || _editor.TextArea(this) is not { } area || !area.Contains(anchor.Center))
        {
            _completion.IsVisible = false;
            return;
        }

        var height = Math.Min(completion.Rows.Count, CompletionWidget.VisibleRows) * CompletionWidget.RowHeight + 2;
        var top = anchor.Bottom + 2;
        if (top + height > Bounds.Height && anchor.Top - height - 2 >= 0)
        {
            top = anchor.Top - height - 2;
        }

        var left = Math.Clamp(anchor.Left - CompletionWidget.LabelInset, 0, Math.Max(0, Bounds.Width - CompletionWidget.ListWidth));
        SetLeft(_completion, left);
        SetTop(_completion, top);
        _completion.IsVisible = true;
        HideHover();
    }

    /// <summary>Above the caret's line (below when there is no room), so the suggestion list below stays visible.</summary>
    private void PlaceSignatureHelp()
    {
        var help = _workbench.SignatureHelp;
        if (!help.IsOpen || !_signature.Show(help) || _editor.CharacterBounds(help.AnchorOffset, this) is not { } caret
            || _editor.TextArea(this) is not { } area || !area.Contains(caret.Center))
        {
            _signature.IsVisible = false;
            return;
        }

        _signature.IsVisible = true; // hidden controls measure as empty
        _signature.Measure(new Size(Math.Max(200, Bounds.Width - 16), Bounds.Height));
        var size = _signature.DesiredSize;
        var top = caret.Top - size.Height - 4;
        if (top < 0)
        {
            top = caret.Bottom + 4;
        }

        SetLeft(_signature, Math.Clamp(caret.Left - 12, 0, Math.Max(0, Bounds.Width - size.Width)));
        SetTop(_signature, top);
        HideHover();
    }

    private async Task ShowHoverAsync(int offset)
    {
        _hideHover.Stop();
        if (_workbench.Editors.ActiveEditor is not { } tab || _workbench.Completion.IsOpen)
        {
            return;
        }

        var generation = ++_hoverGeneration;
        var diagnostics = tab.Diagnostics.Where(marker => marker.Contains(offset)).ToList();
        var hover = await _workbench.Languages.HoverAsync(tab, offset, CancellationToken.None);
        if (generation != _hoverGeneration || !ReferenceEquals(tab, _workbench.Editors.ActiveEditor)
            || !_hover.Show(diagnostics, hover, tab.Language) || _editor.CharacterBounds(offset, this) is not { } word)
        {
            return;
        }

        _hover.IsVisible = true; // hidden controls measure as empty
        _hover.Measure(new Size(Math.Max(200, Bounds.Width - 16), Bounds.Height));
        var size = _hover.DesiredSize;
        var top = word.Top - size.Height - 4;
        if (top < 0)
        {
            top = word.Bottom + 4;
        }

        SetLeft(_hover, Math.Clamp(word.Left - 8, 0, Math.Max(0, Bounds.Width - size.Width)));
        SetTop(_hover, top);
    }

    private void HideHover()
    {
        _hoverGeneration++;
        _hideHover.Stop();
        _hover.IsVisible = false;
    }
}
