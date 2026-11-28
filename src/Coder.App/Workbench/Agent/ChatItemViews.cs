using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Coder.App.Controls;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Agent;

namespace Coder.App.Workbench.Agent;

/// <summary>Builds the view of each kind of chat entry.</summary>
public static class ChatItemViews
{
    public static Control Build(ChatItem item) => item switch
    {
        UserMessageItem user => User(user),
        AssistantMessageItem assistant => Assistant(assistant),
        ToolCallItem tool => Tool(tool),
        NoticeItem notice => Notice(notice),
        _ => new Panel(),
    };

    private static Control User(UserMessageItem item)
    {
        var content = new StackPanel { Spacing = Spacing.S };
        if (item.Text.Length > 0)
        {
            content.Children.Add(new SelectableTextBlock { Text = item.Text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextPrimary, LineHeight = 19.5 });
        }

        if (item.Attachments.Count > 0)
        {
            var chips = new WrapPanel();
            foreach (var attachment in item.Attachments)
            {
                chips.Children.Add(AttachmentChip(attachment, null));
            }

            content.Children.Add(chips);
        }

        // A right-aligned bubble; the smaller bottom-right corner points at its sender.
        var radius = Spacing.RadiusLarge.TopLeft;
        return new Border
        {
            Classes = { "chat-user" },
            Background = Palette.Elevated,
            BorderBrush = Palette.BorderStrong,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(radius, radius, Spacing.RadiusSmall.TopLeft, radius),
            Padding = new Thickness(Spacing.M, Spacing.S),
            Margin = new Thickness(Spacing.XXL, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = content,
        };
    }

    /// <summary>
    /// The agent's reply: a header, its reasoning folded under "Thinking", and the Markdown, drawn again
    /// at most every 50 ms while it streams so long replies stay smooth.
    /// </summary>
    private static Control Assistant(AssistantMessageItem item)
    {
        var markdown = new MarkdownView();
        var working = new TextBlock { Text = "Working…", Foreground = Palette.TextMuted, FontStyle = FontStyle.Italic };
        var reasoningText = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Palette.TextMuted, FontSize = Typography.SmallSize, IsVisible = false, Margin = new Thickness(Spacing.L, 0, 0, 0) };
        var reasoningLabel = new TextBlock { FontSize = Typography.SmallSize, Foreground = Palette.TextSecondary, VerticalAlignment = VerticalAlignment.Center };
        var reasoningChevron = new Icon(Icons.ChevronRight, 12) { Foreground = Palette.TextSecondary };
        var reasoningToggle = new FlatButton
        {
            Padding = new Thickness(2, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { reasoningChevron, reasoningLabel } },
        };
        reasoningToggle.Click += (_, _) =>
        {
            reasoningText.IsVisible = !reasoningText.IsVisible;
            reasoningChevron.Data = reasoningText.IsVisible ? Icons.ChevronDown : Icons.ChevronRight;
        };
        AutomationProperties.SetName(reasoningToggle, "Show reasoning");

        var pending = false;
        void Render()
        {
            pending = false;
            markdown.Show(item.Blocks);
            reasoningToggle.IsVisible = item.HasReasoning;
            reasoningLabel.Text = item.IsThinking ? "Thinking…" : "Reasoning";
            reasoningText.Text = item.Reasoning;
            working.IsVisible = item.IsStreaming && item.Text.Length == 0 && !item.HasReasoning;
        }

        void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!item.IsStreaming)
            {
                Render();
            }
            else if (!pending)
            {
                pending = true;
                DispatcherTimer.RunOnce(Render, TimeSpan.FromMilliseconds(50));
            }
        }

        var root = new StackPanel
        {
            Spacing = Spacing.XS,
            Children = { Header(Icons.Sparkle, "Coder Agent"), working, reasoningToggle, reasoningText, markdown },
        };
        root.Classes.Add("chat-assistant");
        root.AttachedToVisualTree += (_, _) =>
        {
            item.PropertyChanged += OnChanged;
            Render();
        };
        root.DetachedFromVisualTree += (_, _) => item.PropertyChanged -= OnChanged;
        Render();
        return root;
    }

    private static Control Header(Avalonia.Media.Geometry icon, string text) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children =
        {
            new Icon(icon, 14) { Foreground = Palette.AccentBright, VerticalAlignment = VerticalAlignment.Center },
            new TextBlock { Text = text, FontWeight = FontWeight.SemiBold, FontSize = Typography.SmallSize, Foreground = Palette.TextSecondary, VerticalAlignment = VerticalAlignment.Center },
        },
    };

    /// <summary>A tool the agent used: a one-line row (click to see the result), and the edit or command card when there is one.</summary>
    private static Control Tool(ToolCallItem item)
    {
        var status = new TextBlock { Width = 14, FontSize = Typography.SmallSize, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
        var title = new TextBlock { Text = item.Title, FontSize = Typography.SmallSize, Foreground = Palette.TextSecondary, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new Icon(Icons.ChevronRight, 12) { Foreground = Palette.TextMuted };
        var row = new FlatButton
        {
            Padding = new Thickness(2, 2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Command = item.ToggleCommand,
            Content = new DockPanel { Children = { chevron.Dock(Dock.Left), status.Dock(Dock.Left), title } },
        };
        AutomationProperties.SetName(row, item.Title);
        ToolTip.SetTip(row, item.Title);
        var result = new SelectableTextBlock { FontFamily = Typography.Code, FontSize = Typography.CaptionSize, Foreground = Palette.TextSecondary, TextWrapping = TextWrapping.Wrap };
        var resultBox = new Border
        {
            Classes = { "card" },
            Margin = new Thickness(Spacing.L, Spacing.XXS, 0, 0),
            Child = new ScrollViewer { MaxHeight = 220, Padding = new Thickness(Spacing.M, Spacing.S), Content = result },
        };
        var proposal = new ContentControl();
        var root = new StackPanel { Spacing = 2, Children = { row, proposal, resultBox } };
        root.Classes.Add("chat-tool");

        void Update()
        {
            (status.Text, status.Foreground) = item.Status switch
            {
                ToolStatus.Succeeded => ("✓", Palette.Accent),
                ToolStatus.Failed => ("✕", Palette.Danger),
                ToolStatus.WaitingForUser => ("?", Palette.Warning),
                _ => ("•", Palette.TextMuted),
            };
            result.Text = item.Result;
            var isCommand = item.Call.Name == "run_command";

            // A command's output shows by itself once it ran; other results on request.
            resultBox.IsVisible = item.Result.Length > 0 && (item.IsExpanded || (isCommand && item.Proposal is CommandProposalItem { State: ProposalState.Accepted }));
            chevron.Data = resultBox.IsVisible ? Icons.ChevronDown : Icons.ChevronRight;
            if (proposal.Tag != item.Proposal)
            {
                proposal.Tag = item.Proposal;
                proposal.Content = item.Proposal switch
                {
                    EditProposalItem edit => EditCard(edit),
                    CommandProposalItem command => CommandCard(command),
                    PermissionProposalItem permission => PermissionCard(permission),
                    _ => null,
                };
            }
        }

        void OnChanged(object? sender, PropertyChangedEventArgs e) => Update();
        root.AttachedToVisualTree += (_, _) =>
        {
            item.PropertyChanged += OnChanged;
            Update();
        };
        root.DetachedFromVisualTree += (_, _) => item.PropertyChanged -= OnChanged;
        Update();
        return root;
    }

    /// <summary>A proposed edit: the file and its +/− counts, the unified diff, and Accept / Reject (or how it was decided).</summary>
    public static Control EditCard(EditProposalItem edit)
    {
        var lines = new StackPanel();
        foreach (var line in edit.Lines.Take(EditProposalItem.MaxCardLines))
        {
            lines.Children.Add(DiffRow(line));
        }

        if (edit.Lines.Count > EditProposalItem.MaxCardLines)
        {
            lines.Children.Add(new TextBlock { Text = $"… {edit.Lines.Count - EditProposalItem.MaxCardLines} more lines: open the diff to see them all.", Foreground = Palette.TextMuted, FontSize = Typography.SmallSize, Margin = new Thickness(Spacing.S, 2) });
        }

        var counts = new TextBlock { FontSize = Typography.SmallSize, VerticalAlignment = VerticalAlignment.Center };
        counts.Inlines!.Add(new Run($"+{edit.Added}") { Foreground = Palette.Accent });
        counts.Inlines.Add(new Run(" "));
        counts.Inlines.Add(new Run($"−{edit.Removed}") { Foreground = Palette.Danger });

        var fileButton = new FlatButton
        {
            Padding = new Thickness(2, 1),
            Command = edit.OpenFileCommand,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    FileIcons.File(edit.Proposal.Path, 14),
                    new TextBlock { Text = edit.RelativePath, FontSize = Typography.SmallSize, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = edit.IsNewFile ? "new file" : string.Empty, FontSize = Typography.CaptionSize, Foreground = Palette.TextMuted, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        AutomationProperties.SetName(fileButton, $"Open {edit.RelativePath}");
        var openDiff = new FlatButton(Icons.Diff, "Open Diff in Editor", 14) { Command = edit.OpenDiffCommand };

        var accept = new FlatButton { Classes = { "primary" }, Content = "Accept", Command = edit.AcceptCommand, Padding = new Thickness(Spacing.M, 3) };
        var reject = new FlatButton { Classes = { "secondary" }, Content = "Reject", Command = edit.RejectCommand, Padding = new Thickness(Spacing.M, 3) };
        AutomationProperties.SetName(accept, $"Accept change to {edit.RelativePath}");
        AutomationProperties.SetName(reject, $"Reject change to {edit.RelativePath}");
        var outcome = new TextBlock { FontSize = Typography.SmallSize, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.S, Children = { accept, reject } };

        void Update()
        {
            actions.IsVisible = edit.IsPending;
            outcome.IsVisible = !edit.IsPending;
            (outcome.Text, outcome.Foreground) = edit.State switch
            {
                ProposalState.Accepted => ("✓ Accepted and saved", Palette.Accent),
                ProposalState.Rejected => ("Rejected; nothing changed", Palette.TextSecondary),
                ProposalState.Failed => ($"Could not apply: {edit.Error}", Palette.Danger),
                ProposalState.Cancelled => ("Stopped before a decision; nothing changed", Palette.TextMuted),
                _ => (string.Empty, Palette.TextSecondary),
            };
        }

        var card = new Border
        {
            Classes = { "card", "edit-proposal" },
            ClipToBounds = true, // the diff's tinted rows stay inside the rounded corners
            Margin = new Thickness(Spacing.L, Spacing.XXS, 0, Spacing.XXS),
            Child = new DockPanel
            {
                Children =
                {
                    new DockPanel
                    {
                        Margin = new Thickness(Spacing.S, Spacing.XS, Spacing.XS, Spacing.XS),
                        Children = { openDiff.Dock(Dock.Right), counts.Dock(Dock.Right), fileButton },
                    }.Dock(Dock.Top),
                    new Panel { Margin = new Thickness(Spacing.S, Spacing.XS, Spacing.S, Spacing.S), Children = { actions, outcome } }.Dock(Dock.Bottom),
                    new Border
                    {
                        Background = Palette.EditorBackground,
                        BorderBrush = Palette.Border,
                        BorderThickness = new Thickness(0, 1),
                        Child = new ScrollViewer
                        {
                            MaxHeight = 320,
                            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                            Content = lines,
                        },
                    },
                },
            },
        };
        edit.PropertyChanged += (_, _) => Update();
        Update();
        return card;
    }

    private static Control DiffRow(DiffLine line)
    {
        var (sign, background, foreground) = line.Kind switch
        {
            DiffLineKind.Added => ("+", Palette.DiffInserted, Palette.TextPrimary),
            DiffLineKind.Removed => ("−", Palette.DiffRemoved, Palette.TextPrimary),
            DiffLineKind.Gap => ("⋯", (IBrush?)null, Palette.TextMuted),
            _ => (" ", null, Palette.TextSecondary),
        };
        var number = line.NewNumber ?? line.OldNumber;
        return new Border
        {
            Background = background,
            Padding = new Thickness(Spacing.XS, 0, Spacing.S, 0),
            Child = new TextBlock
            {
                FontFamily = Typography.Code,
                FontSize = Typography.SmallSize,
                Foreground = foreground,
                Text = line.Kind == DiffLineKind.Gap
                    ? $"      {sign} {line.Text}"
                    : $"{(number?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),4} {sign} {line.Text}",
            },
        };
    }

    /// <summary>A command waiting for approval: the command line, and Run / Skip (or how it was decided).</summary>
    public static Control CommandCard(CommandProposalItem command)
    {
        var run = new FlatButton { Classes = { "primary" }, Content = "Run", Command = command.RunCommand, Padding = new Thickness(Spacing.M, 3) };
        var skip = new FlatButton { Classes = { "secondary" }, Content = "Skip", Command = command.SkipCommand, Padding = new Thickness(Spacing.M, 3) };
        AutomationProperties.SetName(run, "Run command");
        AutomationProperties.SetName(skip, "Skip command");
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.S, Children = { run, skip } };
        var outcome = new TextBlock { FontSize = Typography.SmallSize, VerticalAlignment = VerticalAlignment.Center };

        void Update()
        {
            actions.IsVisible = command.IsPending;
            outcome.IsVisible = !command.IsPending;
            (outcome.Text, outcome.Foreground) = command.State switch
            {
                ProposalState.Accepted => ("Ran in the workspace folder", Palette.TextSecondary),
                ProposalState.Rejected => ("Skipped; not run", Palette.TextSecondary),
                ProposalState.Cancelled => ("Stopped before a decision; not run", Palette.TextMuted),
                _ => (string.Empty, Palette.TextSecondary),
            };
        }

        var card = new Border
        {
            Classes = { "card", "command-proposal" },
            Margin = new Thickness(Spacing.L, Spacing.XXS, 0, Spacing.XXS),
            Padding = new Thickness(Spacing.M),
            Child = new StackPanel
            {
                Spacing = Spacing.S,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 6,
                        Children =
                        {
                            new Icon(Icons.Terminal, 14) { Foreground = Palette.TextSecondary },
                            new TextBlock { Text = "Run this command?", FontSize = Typography.SmallSize, FontWeight = FontWeight.SemiBold },
                        },
                    },
                    new Border
                    {
                        Background = Palette.EditorBackground,
                        BorderBrush = Palette.Border,
                        BorderThickness = new Thickness(1),
                        CornerRadius = Spacing.RadiusMedium,
                        Padding = new Thickness(Spacing.S, Spacing.XS),
                        Child = new SelectableTextBlock { Text = command.Command, FontFamily = Typography.Code, FontSize = Typography.SmallSize, TextWrapping = TextWrapping.Wrap },
                    },
                    new Panel { Children = { actions, outcome } },
                },
            },
        };
        command.PropertyChanged += (_, _) => Update();
        Update();
        return card;
    }

    /// <summary>
    /// An agent asks permission for the tool above: the file change it would make (when it changes a file),
    /// and the agent's own choices as buttons (or the one picked). The agent runs the tool itself once allowed.
    /// </summary>
    public static Control PermissionCard(PermissionProposalItem permission)
    {
        var choices = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var choice in permission.Choices)
        {
            var button = new FlatButton
            {
                Classes = { choice.IsPrimary ? "primary" : "secondary" },
                Content = choice.Name,
                Command = choice.Command,
                Padding = new Thickness(Spacing.M, 3),
                Margin = new Thickness(0, 0, Spacing.S, Spacing.XS),
            };
            AutomationProperties.SetName(button, choice.Name);
            choices.Children.Add(button);
        }

        var outcome = new TextBlock { FontSize = Typography.SmallSize, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };

        void Update()
        {
            choices.IsVisible = permission.IsPending;
            outcome.IsVisible = !permission.IsPending;
            (outcome.Text, outcome.Foreground) = permission.State switch
            {
                ProposalState.Accepted => ($"✓ {permission.Chosen?.Name}", Palette.Accent),
                ProposalState.Rejected => ($"{permission.Chosen?.Name}: not run", Palette.TextSecondary),
                ProposalState.Cancelled => ("Stopped before a decision; not run", Palette.TextMuted),
                _ => (string.Empty, Palette.TextSecondary),
            };
        }

        var body = new StackPanel { Spacing = Spacing.S };
        if (permission.Change is { } change)
        {
            var counts = new TextBlock { FontSize = Typography.SmallSize, VerticalAlignment = VerticalAlignment.Center };
            counts.Inlines!.Add(new Run($"+{permission.Added}") { Foreground = Palette.Accent });
            counts.Inlines.Add(new Run(" "));
            counts.Inlines.Add(new Run($"−{permission.Removed}") { Foreground = Palette.Danger });
            var fileButton = new FlatButton
            {
                Padding = new Thickness(2, 1),
                Command = permission.OpenFileCommand,
                HorizontalAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        FileIcons.File(change.Path, 14),
                        new TextBlock { Text = permission.RelativePath, FontSize = Typography.SmallSize, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = permission.IsNewFile ? "new file" : string.Empty, FontSize = Typography.CaptionSize, Foreground = Palette.TextMuted, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            AutomationProperties.SetName(fileButton, $"Open {permission.RelativePath}");
            var openDiff = new FlatButton(Icons.Diff, "Open Diff in Editor", 14) { Command = permission.OpenDiffCommand };
            var lines = new StackPanel();
            foreach (var line in permission.Lines.Take(EditProposalItem.MaxCardLines))
            {
                lines.Children.Add(DiffRow(line));
            }

            body.Children.Add(new DockPanel { Children = { openDiff.Dock(Dock.Right), counts.Dock(Dock.Right), fileButton } });
            body.Children.Add(new Border
            {
                Background = Palette.EditorBackground,
                BorderBrush = Palette.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = Spacing.RadiusMedium,
                ClipToBounds = true,
                Child = new ScrollViewer { MaxHeight = 320, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, Content = lines },
            });
        }
        else
        {
            body.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Icon(Icons.Terminal, 14) { Foreground = Palette.TextSecondary },
                    new TextBlock { Text = "Allow this?", FontSize = Typography.SmallSize, FontWeight = FontWeight.SemiBold },
                },
            });
        }

        body.Children.Add(new Panel { Children = { choices, outcome } });
        var card = new Border
        {
            Classes = { "card", "permission-proposal" },
            Margin = new Thickness(Spacing.L, Spacing.XXS, 0, Spacing.XXS),
            Padding = new Thickness(Spacing.M),
            Child = body,
        };
        permission.PropertyChanged += (_, _) => Update();
        Update();
        return card;
    }

    private static Control Notice(NoticeItem item)
    {
        var text = new SelectableTextBlock
        {
            Text = item.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = Typography.SmallSize,
            Foreground = item.IsError ? Palette.DiagnosticError : Palette.TextSecondary,
        };
        var panel = new DockPanel
        {
            Children =
            {
                new Icon(item.IsError ? Icons.Error : Icons.Info, 14)
                {
                    Foreground = item.IsError ? Palette.DiagnosticError : Palette.DiagnosticInfo,
                    Margin = new Thickness(0, 1, Spacing.S, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                }.Dock(Dock.Left),
            },
        };
        if (item.RetryCommand is { } retry)
        {
            var button = new FlatButton { Classes = { "secondary" }, Content = "Retry", Command = retry, Padding = new Thickness(Spacing.S, 2), Margin = new Thickness(Spacing.S, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            panel.Children.Add(button.Dock(Dock.Right));
        }

        panel.Children.Add(text);
        panel.Classes.Add(item.IsError ? "chat-error" : "chat-notice");
        return panel;
    }

    /// <summary>A file, folder or image chip: its icon or thumbnail and name, with a remove button when <paramref name="remove"/> is given.</summary>
    public static Control AttachmentChip(AgentAttachment attachment, System.Windows.Input.ICommand? remove)
    {
        Control icon = attachment.Kind switch
        {
            AttachmentKind.Image when attachment.Data is { } data => Thumbnail(data),
            AttachmentKind.Folder => new Icon(Icons.Folder, 14) { Foreground = Palette.TextSecondary },
            _ => FileIcons.File(attachment.Path ?? attachment.Name, 14),
        };
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                icon,
                new TextBlock { Text = attachment.Name, FontSize = Typography.SmallSize, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        if (remove is not null)
        {
            var button = new FlatButton(Icons.Close, $"Remove {attachment.Name}", 12) { Width = 18, Height = 18, Command = remove, CommandParameter = attachment };
            content.Children.Add(button);
        }

        // A rounded pill (chips are about 22px tall, so the large radius closes the ends).
        var chip = new Border
        {
            Classes = { "attachment-chip" },
            Background = Palette.Hover,
            BorderBrush = Palette.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = Spacing.RadiusLarge,
            Padding = new Thickness(Spacing.S, Spacing.XXS, remove is null ? Spacing.S : Spacing.XXS, Spacing.XXS),
            Margin = new Thickness(0, 0, Spacing.XS, Spacing.XS),
            Child = content,
        };
        ToolTip.SetTip(chip, attachment.Path ?? attachment.Name);
        AutomationProperties.SetName(chip, attachment.Name);
        return chip;
    }

    private static Control Thumbnail(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data);
            return new Image { Source = Bitmap.DecodeToWidth(stream, 32), Width = 16, Height = 16, Stretch = Stretch.UniformToFill };
        }
        catch (Exception)
        {
            return new Icon(Icons.Image, 14) { Foreground = Palette.TextSecondary };
        }
    }
}
