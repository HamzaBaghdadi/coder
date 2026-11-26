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
            .Bind(CodeEditor.ShowMinimapProperty, workbench, x => x.IsMinimapVisible)
            .Bind(CodeEditor.MinimapRenderCharactersProperty, workbench, x => x.MinimapRendersCharacters)
            .Bind(CodeEditor.SearchMatchesProperty, workbench.Find, x => x.Matches)
            .Bind(CodeEditor.CurrentSearchMatchProperty, workbench.Find, x => x.CurrentIndex);
        _editor.SetBlame(workbench.SourceControl.Blame);
        _editor.SetDefinitionSupport(tab => workbench.Languages.Supports(tab, c => c.HasDefinition));

        var images = new ImageViewer()
            .Bind(IsVisibleProperty, editors, x => x.ActiveTab, tab => tab is ImageTabViewModel);
        var settings = new SettingsView()
            .Bind(IsVisibleProperty, editors, x => x.ActiveTab, tab => tab is Presentation.Settings.SettingsPageViewModel);
        _settings = settings;
        _diff = new DiffEditor(workbench.OpenFileAsync)
            .Bind(DiffEditor.IsVisibleProperty, editors, x => x.ActiveTab, tab => tab is DiffTabViewModel);
        editors.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorGroupViewModel.ActiveTab))
            {
                _diff.Diff = editors.ActiveTab as DiffTabViewModel;
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
                Breadcrumbs(editors).Bind(IsVisibleProperty, editors, x => x.ActiveTab, tab => tab is not Presentation.Settings.SettingsPageViewModel).Dock(Dock.Top),
                new Panel
                {
                    Children =
                    {
                        new Panel { Children = { _editor, new ConflictActions(_editor, workbench), new LanguageOverlay(_editor, workbench), new ChangePeek(_editor, workbench), new FindWidget(workbench.Find) } }
                            .Bind(IsVisibleProperty, editors, x => x.ActiveEditor, editor => editor is not null),
                        _diff,
                        images,
                        settings,
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

    private static Control Breadcrumbs(EditorGroupViewModel editors)
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
            Padding = new Thickness(Spacing.L, 0),
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
