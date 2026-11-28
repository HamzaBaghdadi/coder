using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Coder.App.Controls;
using Coder.App.Editor;
using Coder.App.Workbench;
using Coder.Core.Extensions;
using Coder.Core.Files;
using Coder.Core.Lsp;
using Coder.Presentation.Extensions;
using Coder.Presentation.Workbench;

namespace Coder.App.Tests;

public sealed partial class WorkbenchUiTests
{
    /// <summary>
    /// An extension's panel (panel/show): Coder draws its nodes as an editor tab with its own controls; typing,
    /// clicks, dropdowns, tabs and tables report panel/event; new content keeps the controls (the text box being
    /// typed in stays the same one); panel/close closes the tab and the program hears panel/closed.
    /// </summary>
    [AvaloniaFact]
    public async Task Extension_panels_draw_native_controls_and_report_events()
    {
        var root = Directory.CreateTempSubdirectory("coder-panels-").FullName;
        using var http = new HttpClient();
        try
        {
            var store = new ExtensionStore(Path.Combine(root, "installed"), http);
            InstallProgramExtension(store.Folder);
            var workbench = new WorkbenchViewModel(new LocalFileSystem(), new NoDialogs(), new MemorySettingsStore(), extensionStore: store);
            var window = new MainWindow(workbench) { Width = 1280, Height = 800 };
            window.Show();
            var launcher = new FakeLauncher();
            var host = workbench.EnableExtensionHost(launcher);
            await host.StartAsync();
            var program = launcher.Programs.Single();
            await program.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            // A request builder.
            await program.Call("panel/show", new JsonObject { ["panel"] = "helper.request", ["key"] = "r1", ["title"] = "GET Users", ["content"] = RequestPanel("https://api.example.com/users", "{ }") });
            var tab = Assert.IsType<PanelTabViewModel>(workbench.Editors.ActiveTab);
            Assert.Equal(("GET Users", "r1"), (tab.Title, tab.Key));
            Dispatcher.UIThread.RunJobs();
            var panel = window.GetVisualDescendants().OfType<ExtensionPanelView>().First(p => p.IsVisible);
            var url = Assert.IsType<TextBox>(panel.ControlFor("url"));
            Assert.Equal("https://api.example.com/users", url.Text);
            Assert.IsType<CodeEditor>(panel.ControlFor("response"));
            Save(window, "extension-panel");

            // Typing reports a change (after a pause); Enter submits; the button clicks.
            url.Focus();
            url.Text = "https://api.example.com/orders";
            Assert.Equal(("url", "change", "https://api.example.com/orders"), Read(await NextPanelEventAsync(program)));
            ((FlatButton)panel.ControlFor("send")!).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            var click = Read(await NextPanelEventAsync(program));
            Assert.Equal(("send", "click"), (click.Id, click.Type));

            // The dropdown and the tabs.
            var method = Assert.IsType<ComboBox>(panel.ControlFor("method"));
            method.SelectedIndex = 1;
            Assert.Equal(("method", "change", "POST"), Read(await NextPanelEventAsync(program)));

            // New content (the response arrived): the same text box, still holding what was typed.
            await program.Call("panel/show", new JsonObject { ["panel"] = "helper.request", ["key"] = "r1", ["content"] = RequestPanel("https://api.example.com/orders", "{ \"ok\": true }") });
            Dispatcher.UIThread.RunJobs();
            Assert.Same(url, panel.ControlFor("url"));
            Assert.Equal("https://api.example.com/orders", url.Text);
            Assert.Equal("{ \"ok\": true }", ((CodeEditor)panel.ControlFor("response")!).Tab!.Document.GetText());
            Assert.True(((CodeEditor)panel.ControlFor("response")!).Tab!.Document.IsReadOnly);

            // A table cell edit and the add row.
            var cell = Assert.IsType<TextBox>(panel.ControlFor("headers/h1/value"));
            cell.Text = "text/plain";
            var edit = await NextPanelEventAsync(program);
            Assert.Equal(("headers", "change"), (Read(edit).Id, Read(edit).Type));
            Assert.Equal(("h1", "value", "text/plain"), (edit.GetProperty("value").GetProperty("row").GetString(), edit.GetProperty("value").GetProperty("column").GetString(), edit.GetProperty("value").GetProperty("value").GetString()));

            // A second key is a second tab; bad content is an error naming the node.
            await program.Call("panel/show", new JsonObject { ["panel"] = "helper.request", ["key"] = "r2", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "Second" } });
            Assert.Equal(2, workbench.Editors.Tabs.OfType<PanelTabViewModel>().Count());
            var error = await Assert.ThrowsAsync<LanguageServerException>(() => program.Call("panel/show", new JsonObject { ["panel"] = "helper.request", ["content"] = new JsonObject { ["type"] = "button", ["label"] = "No id" } }));
            Assert.Contains("\"id\"", error.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<LanguageServerException>(() => program.Call("panel/show", new JsonObject { ["panel"] = "not.declared", ["content"] = new JsonObject { ["type"] = "spacer" } }));

            // Closing: by the program, and the program hears it.
            var closed = await program.Call("panel/close", new JsonObject { ["panel"] = "helper.request", ["key"] = "r2" });
            Assert.True(closed.GetProperty("closed").GetBoolean());
            var notice = await NextPanelEventAsync(program);
            Assert.Equal(("helper.request", "r2"), (notice.GetProperty("panel").GetString(), notice.GetProperty("key").GetString()));
            Assert.Single(workbench.Editors.Tabs.OfType<PanelTabViewModel>());
            window.Close();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static JsonObject RequestPanel(string url, string response) => (JsonObject)JsonNode.Parse($$"""
        { "type": "column", "padding": 12, "children": [
          { "type": "row", "children": [
            { "type": "select", "id": "method", "value": "GET", "options": [{ "value": "GET", "color": "green" }, { "value": "POST", "color": "orange" }] },
            { "type": "input", "id": "url", "grow": true, "monospace": true, "placeholder": "Enter a URL" },
            { "type": "button", "id": "send", "label": "Send", "style": "primary" } ] },
          { "type": "split", "id": "split", "orientation": "vertical", "grow": true,
            "first": { "type": "tabs", "id": "section", "selected": "headers", "items": [
              { "id": "params", "label": "Params", "content": { "type": "text", "text": "No params", "style": "muted" } },
              { "id": "headers", "label": "Headers", "badge": "1", "content": { "type": "table", "id": "headers", "canAdd": true, "canRemove": true,
                "columns": [{ "id": "on", "kind": "checkbox", "width": 32 }, { "id": "key", "label": "Key" }, { "id": "value", "label": "Value" }],
                "rows": [{ "id": "h1", "cells": { "on": true, "key": "Accept", "value": "application/json" } }] } } ] },
            "second": { "type": "column", "children": [
              { "type": "row", "children": [{ "type": "badge", "text": "200 OK", "color": "green" }, { "type": "text", "text": "184 ms · 312 B", "style": "muted" }] },
              { "type": "code", "id": "response", "language": "json", "readOnly": true, "grow": true, "value": "" } ] } } ] }
        """)!.Also(n =>
    {
        n["children"]![0]!["children"]![1]!["value"] = url;
        n["children"]![1]!["second"]!["children"]![1]!["value"] = response;
    });

    private static async Task<JsonElement> NextPanelEventAsync(FakeProgram program)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (program.PanelEvents.Reader.TryRead(out var e))
            {
                return e;
            }

            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs(); // the typing pause runs on the UI dispatcher
        }

        throw new TimeoutException("No panel event arrived.");
    }

    private static (string Id, string Type, string? Value) Read(JsonElement e) =>
        (e.GetProperty("id").GetString()!, e.GetProperty("type").GetString()!, e.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null);
}

internal static class JsonNodeTestExtensions
{
    public static T Also<T>(this T node, Action<T> change)
    {
        change(node);
        return node;
    }
}
