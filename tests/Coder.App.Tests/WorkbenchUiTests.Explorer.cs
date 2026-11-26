using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Workbench;
using Coder.Core.Files;
using Coder.Presentation.Editors;
using Coder.Presentation.Palette;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>The header's New File: a name typed in place (folders included) creates the file, opens it and selects it.</summary>
    [AvaloniaFact]
    public async Task Explorer_new_file_is_named_in_place_and_opened()
    {
        await WithExplorerAsync(async (window, workbench, folder) =>
        {
            Assert.All(new[] { "New File…", "New Folder…", "Refresh Explorer", "Collapse Folders in Explorer" }, name => Button(window, name));

            Button(window, "New File…").Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "New file name");
            box.Focus();
            box.Text = "notes/todo.md";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Settle();

            var created = Path.Combine(folder, "notes", "todo.md");
            Assert.True(File.Exists(created));
            Assert.Equal(created, workbench.Editors.ActiveEditor?.FilePath);
            Assert.Equal(created, workbench.Explorer.SelectedItem?.Path);
            Assert.DoesNotContain(workbench.Explorer.Rows, row => row.IsEditing || row.IsPending);
            Save(window, "explorer-new-file");
        });
    }

    /// <summary>F2 renames in place and the open tab follows; Delete (after confirming) trashes the file and closes its tab.</summary>
    [AvaloniaFact]
    public async Task Explorer_rename_and_delete_from_the_keyboard()
    {
        await WithExplorerAsync(async (window, workbench, folder) =>
        {
            var file = Path.Combine(folder, "app.ts");
            await workbench.OpenFileAsync(file);
            await Settle();
            var row = workbench.Explorer.Rows.Single(r => r.Name == "app.ts");
            FocusRow(window, workbench, row);

            window.KeyPressQwerty(PhysicalKey.F2, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var box = window.GetVisualDescendants().OfType<TextBox>().Single(b => Avalonia.Automation.AutomationProperties.GetName(b) == "New name");
            Assert.Equal("app", box.SelectedText); // the name, not its extension
            box.Text = "main.ts";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            await Settle();

            var renamed = Path.Combine(folder, "main.ts");
            Assert.True(File.Exists(renamed));
            Assert.False(File.Exists(file));
            Assert.Equal(renamed, workbench.Editors.ActiveEditor?.FilePath);

            FocusRow(window, workbench, workbench.Explorer.Rows.Single(r => r.Name == "main.ts"));
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            await Settle();
            Assert.False(File.Exists(renamed));
            Assert.Empty(workbench.Editors.Tabs);
            Assert.DoesNotContain(workbench.Explorer.Rows, r => r.Name == "main.ts");
        });
    }

    /// <summary>Dropping a file on a folder moves it there; a folder can't be dropped into itself.</summary>
    [AvaloniaFact]
    public async Task Explorer_drag_and_drop_moves_files()
    {
        await WithExplorerAsync(async (window, workbench, folder) =>
        {
            await workbench.OpenFileAsync(Path.Combine(folder, "app.ts"));
            var explorer = workbench.Explorer;
            var view = window.GetVisualDescendants().OfType<ExplorerView>().Single();
            var src = explorer.Rows.Single(r => r.Name == "src");
            Assert.False(explorer.CanMove(src, src));

            await view.DropAsync(explorer.Rows.Single(r => r.Name == "app.ts"), src);
            await Settle();

            var moved = Path.Combine(folder, "src", "app.ts");
            Assert.True(File.Exists(moved));
            Assert.Equal(moved, workbench.Editors.ActiveEditor?.FilePath);
            Assert.Contains(explorer.Rows, r => r.Path == moved);
        });
    }

    /// <summary>Opening a file anywhere (Ctrl+P included) reveals it in the explorer; Ctrl+P rows carry the file's icon.</summary>
    [AvaloniaFact]
    public async Task Opening_a_file_reveals_it_and_quick_open_shows_file_icons()
    {
        await WithExplorerAsync(async (window, workbench, folder) =>
        {
            var deep = Path.Combine(folder, "src", "lib", "util.ts");
            await workbench.OpenFileAsync(deep);
            await Settle();
            Assert.Equal(deep, workbench.Explorer.SelectedItem?.Path);
            Assert.True(workbench.Explorer.Rows.Single(r => r.Name == "lib").IsExpanded);

            workbench.Explorer.CollapseAll();
            Assert.DoesNotContain(workbench.Explorer.Rows, r => r.Depth > 0);

            var provider = new FilePaletteProvider(new LocalFileSystem(), workbench.Explorer, _ => Task.CompletedTask);
            var items = provider.GetItems("util").ToList();
            Assert.NotEmpty(items);
            Assert.All(items, item => Assert.NotNull(item.FilePath));
        });
    }

    private static void FocusRow(Window window, WorkbenchViewModel workbench, Presentation.Explorer.ExplorerItemViewModel row)
    {
        workbench.Explorer.SelectedItem = row;
        Dispatcher.UIThread.RunJobs();
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => Avalonia.Automation.AutomationProperties.GetName(l) == "Files");
        var container = list.ContainerFromItem(row);
        Assert.NotNull(container);
        container.Focus();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(container, window.FocusManager?.GetFocusedElement());
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    /// <summary>A small project in a temporary folder, open in a window. Deleting there deletes for real (no Recycle Bin).</summary>
    private static async Task WithExplorerAsync(Func<MainWindow, WorkbenchViewModel, string, Task> test)
    {
        var folder = Directory.CreateTempSubdirectory("coder-explorer-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "src", "lib"));
            await File.WriteAllTextAsync(Path.Combine(folder, "app.ts"), "export const app = 1;\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(folder, "src", "lib", "util.ts"), "export const util = 2;\n", TestContext.Current.CancellationToken);
            var workbench = new WorkbenchViewModel(new NoTrashFileSystem(), new NoDialogs(), new MemorySettingsStore());
            var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
            workbench.OpenFolder(folder);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await test(window, workbench, folder);
            window.Close();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The disk, except that "move to the Recycle Bin" deletes, so tests leave nothing behind.</summary>
    private sealed class NoTrashFileSystem : IFileSystem
    {
        private readonly LocalFileSystem _disk = new();

        public IReadOnlyList<FileEntry> GetChildren(string directory) => _disk.GetChildren(directory);

        public IEnumerable<string> EnumerateFiles(string root) => _disk.EnumerateFiles(root);

        public Task<TextFileContent> ReadTextAsync(string path, CancellationToken cancellationToken = default) => _disk.ReadTextAsync(path, cancellationToken);

        public Task WriteTextAsync(string path, string text, Encoding encoding, CancellationToken cancellationToken = default) =>
            _disk.WriteTextAsync(path, text, encoding, cancellationToken);

        public bool Exists(string path) => _disk.Exists(path);

        public void CreateFile(string path) => _disk.CreateFile(path);

        public void Move(string from, string to, bool overwrite) => _disk.Move(from, to, overwrite);

        public void Delete(string path, bool recursive) => _disk.Delete(path, recursive);

        public void CreateDirectory(string path) => _disk.CreateDirectory(path);

        public void MoveToTrash(string path) => _disk.Delete(path, recursive: true);
    }
}
