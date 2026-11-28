using System.Text.Json;
using Coder.Core.Extensions;

namespace Coder.Core.Tests;

public sealed class PanelContentTests
{
    private static PanelNode Parse(string json) => PanelContent.Parse(JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 }).RootElement);

    [Fact]
    public void A_request_builder_parses_into_nodes()
    {
        var root = Parse("""
            { "type": "column", "padding": 12, "children": [
              { "type": "row", "children": [
                { "type": "select", "id": "method", "value": "GET", "options": [{ "value": "GET", "color": "green" }, { "value": "POST", "color": "orange" }] },
                { "type": "input", "id": "url", "value": "https://api.example.com", "grow": true, "monospace": true },
                { "type": "button", "id": "send", "label": "Send", "style": "primary" } ] },
              { "type": "split", "orientation": "vertical", "ratio": 0.4,
                "first": { "type": "tabs", "id": "section", "selected": "headers", "items": [
                  { "id": "headers", "label": "Headers", "badge": "2", "content": { "type": "table", "id": "headers", "canAdd": true, "canRemove": true,
                    "columns": [{ "id": "on", "kind": "checkbox", "width": 28 }, { "id": "key", "label": "Key" }, { "id": "value", "label": "Value" }],
                    "rows": [{ "id": "h1", "cells": { "on": true, "key": "Accept", "value": "application/json" } }] } } ] },
                "second": { "type": "code", "id": "response", "value": "{}", "language": "json", "readOnly": true } } ] }
            """);

        var column = Assert.IsType<PanelStack>(root);
        Assert.Equal((false, 12d), (column.Horizontal, column.Padding));
        var row = Assert.IsType<PanelStack>(column.Children[0]);
        var method = Assert.IsType<PanelSelect>(row.Children[0]);
        Assert.Equal(("method", "GET", "green"), (method.Id, method.Value, method.Options[0].Color));
        Assert.True(Assert.IsType<PanelInput>(row.Children[1]).Grow);
        var split = Assert.IsType<PanelSplit>(column.Children[1]);
        Assert.Equal((false, 0.4), (split.Horizontal, split.Ratio));
        var table = Assert.IsType<PanelTable>(Assert.Single(Assert.IsType<PanelTabs>(split.First).Items).Content);
        Assert.Equal(("checkbox", "text"), (table.Columns[0].Kind, table.Columns[1].Kind));
        Assert.True(table.Rows[0].Cells["on"].GetBoolean());
        Assert.True(Assert.IsType<PanelCode>(split.Second).ReadOnly);
    }

    [Fact]
    public void Interactive_nodes_need_an_id_and_errors_say_where()
    {
        var missing = Assert.Throws<ArgumentException>(() => Parse("""{ "type": "column", "children": [{ "type": "text", "text": "a" }, { "type": "button", "label": "Go" }] }"""));
        Assert.Contains("content.children[1]", missing.Message, StringComparison.Ordinal);
        Assert.Contains("\"id\"", missing.Message, StringComparison.Ordinal);

        var style = Assert.Throws<ArgumentException>(() => Parse("""{ "type": "text", "text": "a", "style": "huge" }"""));
        Assert.Contains("isn't one of", style.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => Parse("""{ "type": "widget" }"""));
        Assert.Throws<ArgumentException>(() => Parse("""[1, 2]"""));

        // Read-only code needs no id: it reports nothing.
        Assert.IsType<PanelCode>(Parse("""{ "type": "code", "value": "x", "readOnly": true }"""));
    }

    [Fact]
    public void Trees_nest_and_limits_hold()
    {
        var tree = Assert.IsType<PanelTree>(Parse("""
            { "type": "tree", "id": "collections", "selected": "r1", "items": [
              { "id": "users", "label": "Users", "expanded": true, "children": [
                { "id": "r1", "label": "List users", "badge": { "text": "GET", "color": "green" } } ] } ] }
            """));
        var folder = Assert.Single(tree.Items);
        Assert.True(folder.Expanded);
        Assert.Equal(("GET", "green"), (folder.Children[0].Badge!.Text, folder.Children[0].Badge!.Color));

        var deep = string.Concat(Enumerable.Repeat("""{ "type": "column", "children": [""", PanelContent.MaxDepth + 2)) + """{ "type": "spacer" }""" + new string(']', 0)
                   + string.Concat(Enumerable.Repeat("] }", PanelContent.MaxDepth + 2));
        Assert.Contains("deeper", Assert.Throws<ArgumentException>(() => Parse(deep)).Message, StringComparison.Ordinal);
    }
}
