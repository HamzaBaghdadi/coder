using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.Core.Emmet;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// Built-in Emmet in HTML, with no language server: typing <c>inp</c> lists the abbreviation first and
    /// the snippet names it starts (input, input:b, input:checkbox…), a card beside the list previews the
    /// highlighted expansion, and accepting expands it with tab stops. Tab expands an abbreviation typed
    /// with operators, and plain prose is left alone.
    /// </summary>
    [AvaloniaFact]
    public async Task Emmet_completes_and_expands_abbreviations_in_html()
    {
        var folder = Directory.CreateTempSubdirectory("coder-emmet-").FullName;
        var file = Path.Combine(folder, "index.html");
        await File.WriteAllTextAsync(file, "<!DOCTYPE html>\n<html lang=\"en\">\n<body>\n  \n</body>\n</html>\n");
        var (window, workbench) = CreateWindow();
        try
        {
            workbench.OpenFolder(folder);
            window.Show();
            await workbench.OpenFileAsync(file);
            var tab = workbench.Editors.ActiveEditor!;
            var editor = window.GetVisualDescendants().OfType<Editor.CodeEditor>().First();
            editor.FocusText();
            tab.Editor.SetCaret(tab.Document.GetLineStart(3) + 2);
            Dispatcher.UIThread.RunJobs();

            void Type(string text)
            {
                foreach (var c in text)
                {
                    window.KeyTextInput(c.ToString());
                    Dispatcher.UIThread.RunJobs();
                }
            }

            string Line(int line) => tab.Document.GetLineText(line);

            // "inp": the abbreviation first, then the snippets it starts, all marked as Emmet.
            Type("inp");
            var completion = workbench.Completion;
            await WaitUntil(() => completion.IsOpen, () => "the list did not open");
            var labels = completion.Rows.Select(r => r.Item.Label).ToList();
            Assert.Equal("inp", labels[0]);
            Assert.Equal(["input", "input:b", "input:btn", "input:button", "input:c", "input:checkbox", "input:color", "input:date"], labels.Skip(1).Take(8));
            Assert.All(completion.Rows, r => Assert.Equal(EmmetEngine.ItemDetail, r.Item.Detail));

            var details = window.GetVisualDescendants().OfType<Editor.CompletionDetailsCard>().Single();
            Assert.True(details.IsVisible);
            Assert.Contains(details.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(),
                t => t.Text == "<input type=\"text\" name=\"|\" id=\"|\">");
            Save(window, "emmet-completion");

            // Accepting expands it; name and id are one mirrored stop.
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("  <input type=\"text\" name=\"\" id=\"\">", Line(3));
            Type("email");
            Assert.Equal("  <input type=\"text\" name=\"email\" id=\"email\">", Line(3));
            Assert.False(details.IsVisible);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); // to the end of the snippet, which ends it
            Assert.Equal(tab.Document.GetLineEnd(3), tab.Editor.Selection.Active);
            Assert.False(tab.Editor.IsInSnippet);

            // Tab expands an abbreviation with operators, indented to the line, onto snippet stops.
            tab.Editor.SetCaret(tab.Document.GetLineEnd(3));
            tab.Editor.InsertNewLine();
            Type("ul>li.item$*2");
            workbench.Completion.Close();
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("  <ul>", Line(4));
            var unit = new string(' ', tab.Editor.Options.TabSize); // the editor's indentation, not Emmet's tab
            Assert.Equal($"  {unit}<li class=\"item1\"></li>", Line(5));
            Assert.Equal($"  {unit}<li class=\"item2\"></li>", Line(6));
            Assert.Equal("  </ul>", Line(7));
            Type("one");
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); // next stop
            Type("two");
            Assert.Equal($"  {unit}<li class=\"item1\">one</li>", Line(5));
            Assert.Equal($"  {unit}<li class=\"item2\">two</li>", Line(6));

            // An implicit div with classes.
            tab.Editor.SetCaret(tab.Document.GetLineEnd(7));
            tab.Editor.InsertNewLine();
            Type(".flex.flex-col");
            workbench.Completion.Close();
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("  <div class=\"flex flex-col\"></div>", Line(8));

            // Prose is not an abbreviation: Tab indents.
            tab.Editor.SetCaret(tab.Document.GetLineEnd(8));
            tab.Editor.InsertNewLine();
            Type("hello");
            workbench.Completion.Close();
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("  hello", Line(9), StringComparison.Ordinal);
            Assert.DoesNotContain("<hello>", tab.Document.GetText(), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
            await workbench.DisposeAsync();
            DeleteEventually(folder);
        }
    }
}
