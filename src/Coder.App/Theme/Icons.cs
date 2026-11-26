using Avalonia.Media;

namespace Coder.App.Theme;

/// <summary>
/// The icon set: outline glyphs on a 24×24 grid with round caps (drawn by <see cref="Controls.Icon"/>).
/// </summary>
public static class Icons
{
    public static readonly Geometry Files = Geometry.Parse("M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8Z M14 3v5h5 M9 13h6 M9 17h4");
    public static readonly Geometry Search = Geometry.Parse("M11 4a7 7 0 1 0 0 14a7 7 0 1 0 0-14Z M20 20l-4.2-4.2");
    public static readonly Geometry ChevronRight = Geometry.Parse("M9 6l6 6l-6 6");
    public static readonly Geometry ChevronDown = Geometry.Parse("M6 9l6 6l6 -6");
    public static readonly Geometry Commit = Geometry.Parse("M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6Z M3 12h6 M15 12h6");
    public static readonly Geometry Close = Geometry.Parse("M7 7l10 10 M17 7L7 17");
    public static readonly Geometry File = Geometry.Parse("M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8Z M14 3v5h5");
    public static readonly Geometry Folder = Geometry.Parse("M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z");
    public static readonly Geometry FolderOpen = Geometry.Parse("M3 17V7a2 2 0 0 1 2-2h4l2 2h6a2 2 0 0 1 2 2v1 M3 17l2.5-6a1.5 1.5 0 0 1 1.4-1H21l-2.6 7.1a2 2 0 0 1-1.9 1.4H5a2 2 0 0 1-2-1.5Z");
    public static readonly Geometry NewFile = Geometry.Parse("M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8Z M14 3v5h5 M12 11v6 M9 14h6");
    public static readonly Geometry NewFolder = Geometry.Parse("M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z M12 10.5v5 M9.5 13h5");
    public static readonly Geometry CollapseAll = Geometry.Parse("M8 5.5h10a2.5 2.5 0 0 1 2.5 2.5v10a2.5 2.5 0 0 1-2.5 2.5H8A2.5 2.5 0 0 1 5.5 18V8A2.5 2.5 0 0 1 8 5.5Z M3 15V5.5A2.5 2.5 0 0 1 5.5 3H15 M10 13h6");
    public static readonly Geometry Extensions = Geometry.Parse("M5.5 4h3A1.5 1.5 0 0 1 10 5.5v3A1.5 1.5 0 0 1 8.5 10h-3A1.5 1.5 0 0 1 4 8.5v-3A1.5 1.5 0 0 1 5.5 4Z M5.5 14h3a1.5 1.5 0 0 1 1.5 1.5v3a1.5 1.5 0 0 1-1.5 1.5h-3A1.5 1.5 0 0 1 4 18.5v-3A1.5 1.5 0 0 1 5.5 14Z M15.5 14h3a1.5 1.5 0 0 1 1.5 1.5v3a1.5 1.5 0 0 1-1.5 1.5h-3a1.5 1.5 0 0 1-1.5-1.5v-3a1.5 1.5 0 0 1 1.5-1.5Z M16.9 2.9l2.1 2.1a1.5 1.5 0 0 1 0 2.1L16.9 9.2a1.5 1.5 0 0 1-2.1 0L12.7 7.1a1.5 1.5 0 0 1 0-2.1L14.8 2.9a1.5 1.5 0 0 1 2.1 0Z");
    // A cog, after Lucide's "settings" (ISC license).
    public static readonly Geometry Settings = Geometry.Parse("M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2Z M12 9a3 3 0 1 0 0 6a3 3 0 1 0 0-6Z");
    // A person in a circle, after Lucide's "circle-user" (ISC license): the accounts button.
    public static readonly Geometry Account = Geometry.Parse("M12 2a10 10 0 1 0 0 20a10 10 0 1 0 0-20Z M12 7a3 3 0 1 0 0 6a3 3 0 1 0 0-6Z M7 20.66V19a2 2 0 0 1 2-2h6a2 2 0 0 1 2 2v1.66");
    // A door with an arrow out, after Lucide's "log-out" (ISC license).
    public static readonly Geometry SignOut = Geometry.Parse("M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4 M16 17l5-5l-5-5 M21 12H9");
    public static readonly Geometry Command = Geometry.Parse("M5 7l5 5l-5 5 M12 18h7");
    public static readonly Geometry ArrowUp = Geometry.Parse("M12 19V5 M6 11l6-6l6 6");
    public static readonly Geometry ArrowDown = Geometry.Parse("M12 5v14 M6 13l6 6l6-6");
    public static readonly Geometry Replace = Geometry.Parse("M4 8h12l-3.5-3.5 M20 16H8l3.5 3.5");
    public static readonly Geometry ReplaceAll = Geometry.Parse("M4 6h12l-3-3 M4 12h16 M20 18H8l3 3");
    public static readonly Geometry Plus = Geometry.Parse("M12 5v14 M5 12h14");
    public static readonly Geometry Trash = Geometry.Parse("M5 7h14 M10 11v6 M14 11v6 M7 7l1 13h8l1-13 M9 7V4h6v3");
    public static readonly Geometry Terminal = Geometry.Parse("M6.5 5h11a2.5 2.5 0 0 1 2.5 2.5v9a2.5 2.5 0 0 1-2.5 2.5h-11A2.5 2.5 0 0 1 4 16.5v-9A2.5 2.5 0 0 1 6.5 5Z M8 10l3 2l-3 2 M13 15h3");
    public static readonly Geometry SplitTerminal = Geometry.Parse("M6 5.5h12a2.5 2.5 0 0 1 2.5 2.5v8a2.5 2.5 0 0 1-2.5 2.5H6a2.5 2.5 0 0 1-2.5-2.5V8A2.5 2.5 0 0 1 6 5.5Z M12 5.5v13");
    public static readonly Geometry Error = Geometry.Parse("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18Z M9 9l6 6 M15 9l-6 6");
    public static readonly Geometry Warning = Geometry.Parse("M10.3 5.1a2 2 0 0 1 3.4 0l7.1 12.4a2 2 0 0 1-1.7 3H4.9a2 2 0 0 1-1.7-3Z M12 10v4 M12 17v0.01");
    public static readonly Geometry Info = Geometry.Parse("M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18Z M12 11v5 M12 8v0.01");
    public static readonly Geometry SourceControl = Geometry.Parse("M6 3a2 2 0 1 0 0 4a2 2 0 1 0 0-4Z M6 17a2 2 0 1 0 0 4a2 2 0 1 0 0-4Z M18 3a2 2 0 1 0 0 4a2 2 0 1 0 0-4Z M6 7v10 M18 7v2a4 4 0 0 1-4 4h-4a4 4 0 0 0-4 4");
    public static readonly Geometry Branch = Geometry.Parse("M6 3v12 M18 3a3 3 0 1 0 0 6a3 3 0 1 0 0-6Z M6 15a3 3 0 1 0 0 6a3 3 0 1 0 0-6Z M18 9a9 9 0 0 1-9 9");
    public static readonly Geometry Check = Geometry.Parse("M5 12.5l4.5 4.5L19 7.5");
    public static readonly Geometry Refresh = Geometry.Parse("M20 11a8 8 0 1 0-2.3 5.7 M20 4v7h-7");
    public static readonly Geometry Discard = Geometry.Parse("M9 14L4 9l5-5 M4 9h10.5a5.5 5.5 0 0 1 0 11H11");
    public static readonly Geometry Minus = Geometry.Parse("M5 12h14");
    public static readonly Geometry GoToFile = Geometry.Parse("M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8Z M14 3v5h5 M9 14h6 M12.5 11.5L15 14l-2.5 2.5");
    public static readonly Geometry Diff = Geometry.Parse("M8 4v13 M5 14l3 3l3-3 M16 20V7 M13 10l3-3l3 3");
    public static readonly Geometry Sync = Geometry.Parse("M4 12a8 8 0 0 1 13.7-5.6 M20 4v5h-5 M20 12a8 8 0 0 1-13.7 5.6 M4 20v-5h5");
    public static readonly Geometry Publish = Geometry.Parse("M7 18a4 4 0 0 1-.6-8a6 6 0 0 1 11.4-1.5A4.5 4.5 0 0 1 17.5 18 M12 12v7 M9 15l3-3l3 3");
    // Layout toggles: a window frame with the area outlined (hidden) or filled with strokes (shown).
    public static readonly Geometry LayoutSideBar = Geometry.Parse("M6 5.5h12a2.5 2.5 0 0 1 2.5 2.5v8a2.5 2.5 0 0 1-2.5 2.5H6a2.5 2.5 0 0 1-2.5-2.5V8A2.5 2.5 0 0 1 6 5.5Z M9 5.5v13");
    public static readonly Geometry LayoutSideBarShown = Geometry.Parse("M6 5.5h12a2.5 2.5 0 0 1 2.5 2.5v8a2.5 2.5 0 0 1-2.5 2.5H6a2.5 2.5 0 0 1-2.5-2.5V8A2.5 2.5 0 0 1 6 5.5Z M9 5.5v13 M5.3 7.5v9 M7.2 7.5v9");
    public static readonly Geometry LayoutPanel = Geometry.Parse("M6 5.5h12a2.5 2.5 0 0 1 2.5 2.5v8a2.5 2.5 0 0 1-2.5 2.5H6a2.5 2.5 0 0 1-2.5-2.5V8A2.5 2.5 0 0 1 6 5.5Z M3.5 13h17");
    public static readonly Geometry LayoutPanelShown = Geometry.Parse("M6 5.5h12a2.5 2.5 0 0 1 2.5 2.5v8a2.5 2.5 0 0 1-2.5 2.5H6a2.5 2.5 0 0 1-2.5-2.5V8A2.5 2.5 0 0 1 6 5.5Z M3.5 13h17 M5.5 15h13 M5.5 16.8h13");
    public static readonly Geometry LayoutSecondary = Geometry.Parse("M6 5.5h12a2.5 2.5 0 0 1 2.5 2.5v8a2.5 2.5 0 0 1-2.5 2.5H6a2.5 2.5 0 0 1-2.5-2.5V8A2.5 2.5 0 0 1 6 5.5Z M15 5.5v13");
    public static readonly Geometry LayoutSecondaryShown = Geometry.Parse("M6 5.5h12a2.5 2.5 0 0 1 2.5 2.5v8a2.5 2.5 0 0 1-2.5 2.5H6a2.5 2.5 0 0 1-2.5-2.5V8A2.5 2.5 0 0 1 6 5.5Z M15 5.5v13 M16.8 7.5v9 M18.7 7.5v9");
    // Coder Agent.
    public static readonly Geometry Sparkle = Geometry.Parse("M11 3.5l1.9 5.1L18 10.5l-5.1 1.9L11 17.5l-1.9-5.1L4 10.5l5.1-1.9Z M18.5 15l.8 2.2l2.2.8l-2.2.8l-.8 2.2l-.8-2.2l-2.2-.8l2.2-.8Z");
    public static readonly Geometry Send = Geometry.Parse("M12 19V5 M6 11l6-6l6 6");
    public static readonly Geometry Stop = Geometry.Parse("M8 7h8a1 1 0 0 1 1 1v8a1 1 0 0 1-1 1H8a1 1 0 0 1-1-1V8a1 1 0 0 1 1-1Z");
    public static readonly Geometry Attach = Geometry.Parse("M20 11.5l-8 8a5 5 0 0 1-7-7l8.5-8.5a3.3 3.3 0 0 1 4.7 4.7l-8.5 8.5a1.7 1.7 0 0 1-2.4-2.4l7.8-7.8");
    public static readonly Geometry Image = Geometry.Parse("M6 4h12a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Z M9 8a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3Z M20 15l-4.5-4.5L6 20");
    public static readonly Geometry Copy = Geometry.Parse("M10 9h8a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1h-8a1 1 0 0 1-1-1v-9a1 1 0 0 1 1-1Z M15 9V6a1 1 0 0 0-1-1H6a1 1 0 0 0-1 1v9a1 1 0 0 0 1 1h3");
    public static readonly Geometry Key = Geometry.Parse("M8 11a4 4 0 1 0 0 8a4 4 0 1 0 0-8Z M10.8 12.2L19 4 M16 7l2.5 2.5 M14 9l2 2");
    public static readonly Geometry Minimize = Geometry.Parse("M6 12h12");
    public static readonly Geometry Maximize = Geometry.Parse("M8.5 6.5h7a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2h-7a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2Z");
    public static readonly Geometry Restore = Geometry.Parse("M8 9h5.5A1.5 1.5 0 0 1 15 10.5V16a1.5 1.5 0 0 1-1.5 1.5H8A1.5 1.5 0 0 1 6.5 16v-5.5A1.5 1.5 0 0 1 8 9Z M9 9V8a1.5 1.5 0 0 1 1.5-1.5H16A1.5 1.5 0 0 1 17.5 8v5.5A1.5 1.5 0 0 1 16 15h-1");
}
