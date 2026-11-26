using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Agent;
using Coder.Presentation.Mvvm;

namespace Coder.App.Workbench.Agent;

/// <summary>
/// Coder Agent in the secondary side bar: the conversation (replies stream in as Markdown, tools show as
/// rows, edits as diffs to accept, commands as cards to approve), and below it the input with its
/// attachments, the model picker and Send / Stop. Files and images can be dropped on it from the
/// explorer, a tab or Windows, and images pasted with Ctrl+V.
/// </summary>
public sealed class AgentView : DockPanel, IPathDropTarget
{
    private const double BottomSnap = 48;

    private readonly AgentViewModel _agent;
    private readonly TextBox _input;
    private readonly ScrollViewer _scroller;
    private readonly Border _dropHint;
    private bool _stickToBottom = true;

    public AgentView(AgentViewModel agent)
    {
        _agent = agent;
        Background = Palette.SideBar;
        DragDrop.SetAllowDrop(this, true);

        var newChat = new FlatButton(Icons.Plus, "New Chat") { Command = agent.NewChatCommand };
        var providers = new FlatButton(Icons.Key, "Providers and API Keys") { Command = agent.ToggleProvidersCommand }
            .BindClass("active", agent, a => a.IsProvidersOpen);
        Children.Add(SideBarHeader.Create("CODER AGENT", newChat, providers).Dock(Dock.Top));

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            MaxHeight = 240,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(Spacing.S, Spacing.S, Spacing.S, Spacing.XS),
            PlaceholderText = "Ask Coder Agent… Drop files or paste images",
        }.Bind(TextBox.TextProperty, agent, x => x.Input, Avalonia.Data.BindingMode.TwoWay);
        AutomationProperties.SetName(_input, "Message Coder Agent");
        _input.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);

        _scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = new ItemsControl
            {
                ItemsSource = agent.Items,
                Margin = new Thickness(Spacing.M, Spacing.S, Spacing.M, Spacing.M),
                ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Spacing = Spacing.M }),
                ItemTemplate = Templates.Row<ChatItem>(ChatItemViews.Build),
            },
        }.Bind(IsVisibleProperty, agent, x => x.HasMessages);
        _scroller.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y != 0 || e.ViewportDelta.Y != 0)
            {
                if (_stickToBottom)
                {
                    _scroller.ScrollToEnd();
                }
            }
            else if (e.OffsetDelta.Y != 0)
            {
                _stickToBottom = _scroller.Offset.Y >= _scroller.Extent.Height - _scroller.Viewport.Height - BottomSnap;
            }
        };
        agent.Items.CollectionChanged += (_, e) =>
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add && e.NewItems?[0] is UserMessageItem)
            {
                _stickToBottom = true; // the user's own message always comes into view
            }
        };

        _dropHint = new Border
        {
            Classes = { "drop-hint" },
            IsVisible = false,
            IsHitTestVisible = false,
            Background = Palette.Selected,
            BorderBrush = Palette.Accent,
            BorderThickness = new Thickness(2),
            Margin = new Thickness(Spacing.S),
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = Spacing.S,
                Children =
                {
                    new Icon(Icons.Attach, 28) { Foreground = Palette.Accent, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = "Drop to attach to the chat", FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                },
            },
        };

        var chat = new DockPanel
        {
            Children =
            {
                Composer().Dock(Dock.Bottom),
                new Panel { Children = { EmptyState(), _scroller } },
            },
        }.Bind(IsVisibleProperty, agent, x => x.IsProvidersOpen, open => !open);

        var providersPage = new AgentProvidersView(agent).Bind(IsVisibleProperty, agent, x => x.IsProvidersOpen);
        Children.Add(new Panel { Children = { chat, providersPage, _dropHint } });

        AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetPathDragOver(false));
        AddHandler(DragDrop.DropEvent, OnDrop);
        agent.FocusRequested += (_, _) => Dispatcher.UIThread.Post(() => _input.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>The message box (for tests).</summary>
    internal TextBox Input => _input;

    internal bool IsDropHintVisible => _dropHint.IsVisible;

    private Control Composer()
    {
        var chips = new ItemsControl
        {
            ItemsSource = _agent.Attachments,
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel()),
            ItemTemplate = Templates.Row<AgentAttachment>(a => ChatItemViews.AttachmentChip(a, _agent.RemoveAttachmentCommand)),
            Margin = new Thickness(Spacing.S, Spacing.S, Spacing.S, 0),
        };
        chips.IsVisible = _agent.Attachments.Count > 0;
        _agent.Attachments.CollectionChanged += (_, _) => chips.IsVisible = _agent.Attachments.Count > 0;

        var notice = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = Typography.SmallSize,
            Foreground = Palette.Warning,
            Margin = new Thickness(Spacing.XS, 0, Spacing.XS, Spacing.XS),
        }.Bind(TextBlock.TextProperty, _agent, x => x.Notice)
         .Bind(IsVisibleProperty, _agent, x => x.Notice, text => !string.IsNullOrEmpty(text));

        var attach = new FlatButton(Icons.Attach, "Attach Files…", 15);
        attach.Click += async (_, _) => await PickFilesAsync();

        var modelLabel = new TextBlock { FontSize = Typography.SmallSize, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center }
            .Bind(TextBlock.TextProperty, _agent, x => x.ModelLabel);
        var model = new FlatButton
        {
            Padding = new Thickness(6, 2),
            Command = _agent.PickModelCommand,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Children = { modelLabel, new Icon(Icons.ChevronDown, 12) { Foreground = Palette.TextSecondary } },
            },
        };
        AutomationProperties.SetName(model, "Pick Model");
        model.Bind(ToolTip.TipProperty, _agent, x => x.ModelLabel, label => $"Model: {label}");

        var send = new FlatButton(Icons.Send, "Send (Enter)", 16) { Classes = { "primary" }, Command = _agent.SendCommand }
            .Bind(IsVisibleProperty, _agent, x => x.IsBusy, busy => !busy)
            .Bind(IsEnabledProperty, _agent, x => x.CanSend);
        var stop = new FlatButton(Icons.Stop, "Stop (Escape)", 16) { Command = _agent.StopCommand, BorderBrush = Palette.BorderStrong }
            .Bind(IsVisibleProperty, _agent, x => x.IsBusy);
        var usage = new TextBlock { FontSize = Typography.CaptionSize, Foreground = Palette.TextMuted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, Spacing.S, 0) }
            .Bind(TextBlock.TextProperty, _agent, x => x.Usage);

        var toolbar = new DockPanel
        {
            Margin = new Thickness(Spacing.XS, 0, Spacing.XS, Spacing.XS),
            Children =
            {
                attach.Dock(Dock.Left),
                new Panel { Children = { send, stop } }.Dock(Dock.Right),
                usage.Dock(Dock.Right),
                model,
            },
        };

        var box = new ChamferBorder
        {
            Classes = { "composer" },
            Background = Palette.EditorBackground,
            BorderBrush = Palette.InputBorder,
            BorderThickness = 1,
            Chamfer = Spacing.ChamferMedium,
            Child = new DockPanel
            {
                Children =
                {
                    chips.Dock(Dock.Top),
                    toolbar.Dock(Dock.Bottom),
                    _input,
                },
            },
        };
        _input.GotFocus += (_, _) => box.BorderBrush = Palette.Accent;
        _input.LostFocus += (_, _) => box.BorderBrush = Palette.InputBorder;

        return new StackPanel
        {
            Margin = new Thickness(Spacing.S, 0, Spacing.S, Spacing.S),
            Children = { notice, box },
        };
    }

    private Control EmptyState()
    {
        var ready = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = Typography.SmallSize, Foreground = Palette.TextSecondary, TextAlignment = TextAlignment.Center };
        void UpdateReady()
        {
            var usable = _agent.Providers.Where(p => p.IsReady).Select(p => p.Config.Name).ToList();
            ready.Text = usable.Count == 0
                ? "No provider is ready yet: add an API key for FreeLLMAPI or OpenRouter, or start Ollama."
                : $"Ready: {string.Join(", ", usable)}.";
        }

        _agent.Providers.CollectionChanged += (_, _) => UpdateReady();
        _agent.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AgentViewModel.IsProvidersOpen))
            {
                UpdateReady();
            }
        };
        UpdateReady();

        var setup = new FlatButton
        {
            Classes = { "primary" },
            Content = "Set Up Providers",
            HorizontalAlignment = HorizontalAlignment.Center,
            Command = _agent.ToggleProvidersCommand,
        };
        return new StackPanel
        {
            Classes = { "agent-empty" },
            Margin = new Thickness(Spacing.L, Spacing.XL),
            Spacing = Spacing.M,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Icon(Icons.Sparkle, 36) { Foreground = Palette.AccentBright, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Coder Agent", FontSize = 16, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock
                {
                    Text = "Ask about your code, or ask for a change. The agent reads and searches the open folder; every edit comes to you as a diff to accept, and every command waits for your OK.",
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Foreground = Palette.TextSecondary,
                    LineHeight = 19,
                },
                new TextBlock
                {
                    Text = "Drag files here from the explorer, a tab or Windows, or paste an image.",
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    FontSize = Typography.SmallSize,
                    Foreground = Palette.TextMuted,
                },
                ready,
                setup,
            },
        }.Bind(IsVisibleProperty, _agent, x => x.HasMessages, has => !has);
    }

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            if (!_agent.IsBusy)
            {
                _agent.SendCommand.Execute(null);
            }
        }
        else if (e.Key == Key.Escape && _agent.IsBusy)
        {
            e.Handled = true;
            _agent.Stop();
        }
        else if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            _ = PasteAsync();
        }
    }

    /// <summary>Ctrl+V: an image or copied files are attached; text is pasted as usual.</summary>
    private async Task PasteAsync()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        try
        {
            if (await clipboard.TryGetFilesAsync() is { Length: > 0 } files)
            {
                await AttachAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
                return;
            }

            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap)
                {
                    _agent.AddImage(_agent.NextPastedImageName(), "image/png", Png(bitmap));
                }

                return;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An odd clipboard format: fall back to pasting text.
        }

        _input.Paste();
    }

    private async Task PickFilesAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Attach Files to the Chat", AllowMultiple = true });
        await AttachAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
    }

    // Drag and drop --------------------------------------------------------------------------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var data = e.DataTransfer;
        var accepts = data.Contains(DataFormat.File) || data.Contains(DataFormat.Bitmap) || data.Contains(DataFormat.Text);
        e.DragEffects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        SetPathDragOver(accepts);
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        SetPathDragOver(false);
        e.Handled = true;
        var data = e.DataTransfer;
        try
        {
            if (data.TryGetFiles() is { Length: > 0 } files)
            {
                await AttachAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
            }
            else if (data.TryGetBitmap() is { } bitmap)
            {
                using (bitmap)
                {
                    _agent.AddImage(_agent.NextPastedImageName(), "image/png", Png(bitmap));
                }
            }
            else if (data.TryGetText() is { Length: > 0 } text)
            {
                _agent.InsertText(text);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _agent.Notice = $"Could not attach: {exception.Message}";
        }

        _input.Focus();
    }

    public void SetPathDragOver(bool isOver) => _dropHint.IsVisible = isOver;

    public async Task DropPathsAsync(IReadOnlyList<string> paths)
    {
        SetPathDragOver(false);
        await AttachAsync(paths);
        _input.Focus();
    }

    /// <summary>Attaches paths; images the APIs don't take (BMP, TIFF, icons) are converted to PNG first.</summary>
    private async Task AttachAsync(IReadOnlyList<string> paths)
    {
        var plain = new List<string>();
        foreach (var path in paths)
        {
            if (AgentAttachment.IsConvertibleImage(path) && File.Exists(path))
            {
                try
                {
                    using var bitmap = new Bitmap(path);
                    _agent.AddImage(Path.ChangeExtension(Path.GetFileName(path), ".png"), "image/png", Png(bitmap), path);
                }
                catch (Exception)
                {
                    plain.Add(path); // not an image after all: attached (or refused) as a file
                }
            }
            else
            {
                plain.Add(path);
            }
        }

        if (plain.Count > 0)
        {
            await _agent.AddPathsAsync(plain);
        }
    }

    private static byte[] Png(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }
}
