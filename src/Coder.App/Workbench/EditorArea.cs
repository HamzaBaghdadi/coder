using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Coder.App.Controls;
using Coder.App.Editor;
using Coder.App.Mvvm;
using Coder.App.Theme;
using Coder.Presentation.Editors;
using Coder.Presentation.Workbench;

namespace Coder.App.Workbench;

/// <summary>Tabs, breadcrumbs and the code editor; the welcome page when nothing is open.</summary>
public sealed class EditorArea : Panel
{
    private readonly CodeEditor _editor;
    private readonly DiffEditor _diff;
    private readonly SettingsView _settings;
    private readonly EditorGroupViewModel _editors;

    public EditorArea(WorkbenchViewModel workbench)
    {
        var editors = _editors = workbench.Editors;
        _editor = new CodeEditor()
            .Bind(CodeEditor.TabProperty, editors, x => x.ActiveEditor)
            .Bind(CodeEditor.EditorFontSizeProperty, workbench, x => x.EditorFontSize)
            .Bind(CodeEditor.EditorFontFamilyProperty, workbench, x => x.EditorFontFamily)
            .Bind(CodeEditor.EditorLineHeightProperty, workbench, x => x.EditorLineHeight)
            .Bind(CodeEditor.CursorStyleProperty, workbench, x => x.CursorStyle)
            .Bind(CodeEditor.CursorBlinkingProperty, workbench, x => x.CursorBlinking)
            .Bind(CodeEditor.ShowMinimapProperty, workbench, x => x.IsMinimapVisible)
            .Bind(CodeEditor.MinimapRenderCharactersProperty, workbench, x => x.MinimapRendersCharacters)
            .Bind(CodeEditor.StickyScrollProperty, workbench.StickyScroll, x => x.IsEnabled)
            .Bind(CodeEditor.SearchMatchesProperty, workbench.Find, x => x.Matches)
            .Bind(CodeEditor.CurrentSearchMatchProperty, workbench.Find, x => x.CurrentIndex);
        _editor.SetBlame(workbench.SourceControl.Blame);
        _editor.SetInlineSuggestions(workbench.InlineSuggestions);
        _editor.SetDefinitionSupport(tab => workbench.Languages.Supports(tab, c => c.HasDefinition));
        _editor.View.RegisterColorPicker(workbench.Commands);

        var images = new ImageViewer()
            .Bind(IsVisibleProperty, editors, x => x.ActiveTab, tab => tab is ImageTabViewModel);
        var settings = new SettingsView()
            .Bind(IsVisibleProperty, editors, x => x.ActiveTab, tab => tab is Presentation.Settings.SettingsPageViewModel);
        _settings = settings;
        _diff = new DiffEditor(workbench.OpenFileAsync)
            .Bind(DiffEditor.IsVisibleProperty, editors, x => x.ActiveTab, tab => tab is DiffTabViewModel);
        var preview = new PreviewView(workbench.OpenFileAsync)
            .Bind(IsVisibleProperty, editors, x => x.ActiveTab, tab => tab is PreviewTabViewModel);
        var extension = new ExtensionDetailsView(workbench.OpenFileAsync)
            .Bind(IsVisibleProperty, editors, x => x.ActiveTab, tab => tab is Presentation.Extensions.ExtensionDetailsTabViewModel);
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveTab))
            {
                _diff.Diff = editors.ActiveTab as DiffTabViewModel;
                preview.Tab = editors.ActiveTab as PreviewTabViewModel;
                extension.Tab = editors.ActiveTab as Presentation.Extensions.ExtensionDetailsTabViewModel;
                if (editors.ActiveTab is Presentation.Settings.SettingsPageViewModel page)
                {
                    settings.Page = page;
                }
                images.Path = (editors.ActiveTab as ImageTabViewModel)?.FilePath; // decodes only the shown image
            }
        };

        var documentView = new DockPanel
        {
            Children =
            {
                new EditorTabStrip(editors).Dock(Dock.Top),
                Breadcrumbs(workbench).Bind(IsVisibleProperty, editors, x => x.ActiveTab, tab => tab is not Presentation.Settings.SettingsPageViewModel).Dock(Dock.Top),
                new Panel
                {
                    Children =
                    {
                        new Panel { Children = { _editor, new ConflictActions(_editor, workbench), new LanguageOverlay(_editor, workbench), new ChangePeek(_editor, workbench), new FindWidget(workbench.Find) } }
                            .Bind(IsVisibleProperty, editors, x => x.ActiveEditor, editor => editor is not null),
                        _diff,
                        images,
                        settings,
                        preview,
                        extension,
                    },
                },
            },
        }.Bind(IsVisibleProperty, editors, x => x.HasTabs);

        // Esc in the text closes find; closing find returns the keyboard to the text.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Escape && workbench.Find.IsOpen)
            {
                workbench.Find.Close();
                e.Handled = true;
            }
        });
        workbench.Find.Closed += (_, _) => FocusEditor();

        var welcome = new WelcomeView(workbench)
            .Bind(IsVisibleProperty, editors, x => x.HasTabs, hasTabs => !hasTabs);

        Children.Add(welcome);
        Children.Add(documentView);

        // Opening or switching a document puts the keyboard in it, ready to type.
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveTab) && editors.ActiveTab is not null)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(FocusEditor);
            }
        };
    }

    /// <summary>Puts the keyboard in the shown tab: the text of a document, or the diff.</summary>
    public void FocusEditor()
    {
        if (_editors.ActiveTab is DiffTabViewModel)
        {
            _diff.FocusView();
        }
        else if (_editors.ActiveTab is Presentation.Settings.SettingsPageViewModel)
        {
            _settings.FocusSearch();
        }
        else
        {
            _editor.FocusText();
        }
    }

    /// <summary>
    /// The path of the shown file, with the view switch on the right: "Open Preview" on a Markdown or SVG file,
    /// "Open Source" on its preview (like VS Code's editor title buttons).
    /// </summary>
    private static Control Breadcrumbs(WorkbenchViewModel workbench)
    {
        var editors = workbench.Editors;
        var openPreview = new FlatButton(Icons.Preview, "Open Preview (Ctrl+Shift+V)", 14)
        {
            Width = 22,
            Height = 20,
            Command = workbench.Commands.ToCommand(Presentation.Commands.CommandIds.OpenPreview),
        }.Bind(IsVisibleProperty, editors, x => x.ActiveTab, tab => tab is EditorTabViewModel { FilePath: { } path } && PreviewTabViewModel.KindOf(path) is not null);
        var openSource = new FlatButton(Icons.Code, "Open Source", 14)
        {
            Width = 22,
            Height = 20,
            Command = workbench.Commands.ToCommand(Presentation.Commands.CommandIds.OpenPreviewSource),
        }.Bind(IsVisibleProperty, editors, x => x.ActiveTab, tab => tab is PreviewTabViewModel);
        var bar = (Border)PathBar(editors);
        var path = bar.Child!;
        bar.Child = null; // re-parented below
        bar.Padding = new Thickness(Spacing.M + Spacing.XXS, 0, Spacing.XS, 0);
        bar.Child = new DockPanel
        {
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = Spacing.XXS, Children = { openPreview, openSource } }.Dock(Dock.Right),
                path,
            },
        };
        return bar;
    }

    private static Control PathBar(EditorGroupViewModel editors)
    {
        var text = new TextBlock
        {
            FontSize = Typography.SmallSize,
            Foreground = Palette.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.PathSegmentEllipsis,
        }.Bind(TextBlock.TextProperty, editors, x => x.ActiveTab, tab => FormatPath(tab));

        return new Border
        {
            Height = 24,
            Padding = new Thickness(Spacing.M + Spacing.XXS, 0), // lines up with the tab titles above
            Background = Palette.EditorBackground,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new Icon(Icons.File, 13) { Foreground = Palette.TextSecondary }, text },
            },
        };
    }

    /// <summary>"src › App › Program.cs" for the last few path segments.</summary>
    private static string FormatPath(TabViewModel? tab)
    {
        if (tab?.FilePath is not { } path)
        {
            return tab?.Title ?? string.Empty;
        }

        var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Join("  ›  ", segments.TakeLast(4));
    }
}
