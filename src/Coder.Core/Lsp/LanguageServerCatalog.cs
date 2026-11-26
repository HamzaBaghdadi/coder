namespace Coder.Core.Lsp;

/// <summary>
/// How to run a language server: the command, the languages it serves, the files that mark a
/// project root for it, and how to install it when it is missing.
/// </summary>
public sealed record LanguageServerDefinition(
    string Name,
    IReadOnlyList<string> LanguageIds,
    string Command,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> RootMarkers,
    string InstallHint)
{
    /// <summary>
    /// What the server gets when it asks for its configuration (<c>workspace/configuration</c>), as JSON keyed by
    /// section ("gopls": {…}). Used to turn on features that are off by default, such as inlay hints.
    /// </summary>
    public string? Settings { get; init; }

    /// <summary>Options sent with <c>initialize</c> (JSON), for servers that are configured that way.</summary>
    public string? InitializationOptions { get; init; }

    /// <summary>
    /// When Coder ships the server (see <see cref="ManagedServers"/>): it runs that copy with Node.js, and installs
    /// it itself when it is missing. <see cref="Command"/> on PATH is used when there is no such copy.
    /// </summary>
    public NpmTool? Tool { get; init; }
}

/// <summary>Well-known language servers. Each is used when its command is installed.</summary>
public static class LanguageServerCatalog
{
    public static IReadOnlyList<LanguageServerDefinition> Defaults { get; } =
    [
        new("gopls", ["go"], "gopls", [], ["go.work", "go.mod"], "go install golang.org/x/tools/gopls@latest")
        {
            Settings = """{ "gopls": { "hints": { "assignVariableTypes": true, "compositeLiteralFields": true, "constantValues": true, "functionTypeParameters": true, "parameterNames": true, "rangeVariableTypes": true } } }""",
        },
        new("rust-analyzer", ["rust"], "rust-analyzer", [], ["Cargo.toml"], "rustup component add rust-analyzer"),
        new("typescript-language-server", ["typescript", "javascript"], "typescript-language-server", ["--stdio"],
            ["tsconfig.json", "jsconfig.json", "package.json"], "npm install -g typescript-language-server typescript")
        {
            Tool = ManagedTools.TypeScriptServer,
            InitializationOptions = """{ "preferences": { "includeInlayParameterNameHints": "literals", "includeInlayVariableTypeHints": false, "includeInlayFunctionLikeReturnTypeHints": false } }""",
        },
        new("pyright", ["python"], "pyright-langserver", ["--stdio"],
            ["pyrightconfig.json", "pyproject.toml", "setup.py", "requirements.txt"], "npm install -g pyright"),
        new("clangd", ["c", "cpp"], "clangd", [], ["compile_commands.json", "CMakeLists.txt", ".clangd"], "install clangd from https://clangd.llvm.org"),
        new("csharp-ls", ["csharp"], "csharp-ls", [], ["*.sln", "*.slnx", "*.csproj"], "dotnet tool install --global csharp-ls"),
        new("lua-language-server", ["lua"], "lua-language-server", [], [".luarc.json"], "install lua-language-server"),
        new("bash-language-server", ["shellscript"], "bash-language-server", ["start"], [], "npm install -g bash-language-server"),
        new("yaml-language-server", ["yaml"], "yaml-language-server", ["--stdio"], [], "npm install -g yaml-language-server"),
        new("vscode-json-language-server", ["json"], "vscode-json-language-server", ["--stdio"], ["package.json"], "npm install -g vscode-langservers-extracted")
        {
            Tool = ManagedTools.JsonServer,
            InitializationOptions = """{ "provideFormatter": true }""",
        },
        new("vscode-css-language-server", ["css"], "vscode-css-language-server", ["--stdio"], ["package.json"], "npm install -g vscode-langservers-extracted")
        {
            Tool = ManagedTools.CssServer,
            InitializationOptions = """{ "provideFormatter": true }""",
        },
        new("vscode-html-language-server", ["html"], "vscode-html-language-server", ["--stdio"], ["package.json"], "npm install -g vscode-langservers-extracted")
        {
            Tool = ManagedTools.HtmlServer,
            InitializationOptions = """{ "provideFormatter": true, "embeddedLanguages": { "css": true, "javascript": true } }""",
        },
        new("dart", ["dart"], "dart", ["language-server", "--protocol=lsp"], ["pubspec.yaml"], "install the Dart SDK"),
    ];

    /// <summary>
    /// Tailwind CSS IntelliSense: class name completions with their CSS, hovers, color swatches in the
    /// completion list, and lint for conflicting classes. It runs beside a file's main server.
    /// </summary>
    public static LanguageServerDefinition Tailwind { get; } = new(
        "tailwindcss-language-server",
        ["html", "css", "javascript", "typescript", "markdown", "php", "vue"],
        "tailwindcss-language-server",
        ["--stdio"],
        ["tailwind.config.js", "tailwind.config.cjs", "tailwind.config.mjs", "tailwind.config.ts", "package.json"],
        "npm install -g @tailwindcss/language-server")
    {
        Tool = ManagedTools.TailwindServer,
        Settings = """
            {
              "tailwindCSS": {
                "emmetCompletions": false, "includeLanguages": {}, "classAttributes": ["class", "className", "class:list", "classList", "ngClass"],
                "classFunctions": [], "colorDecorators": true, "showPixelEquivalents": true, "rootFontSize": 16, "hovers": true,
                "suggestions": true, "codeActions": true, "validate": true,
                "lint": {
                  "cssConflict": "warning", "invalidApply": "error", "invalidScreen": "error", "invalidVariant": "error",
                  "invalidConfigPath": "error", "invalidTailwindDirective": "error", "invalidSourceDirective": "error", "recommendedVariantOrder": "warning"
                },
                "experimental": { "classRegex": [], "configFile": null },
                "files": { "exclude": ["**/.git/**", "**/node_modules/**", "**/.hg/**", "**/.svn/**"] }
              },
              "editor": { "tabSize": 2 }
            }
            """,
    };

    /// <summary>Servers that run beside a file's main server, each for the projects that use it.</summary>
    public static IReadOnlyList<(LanguageServerDefinition Server, Func<string, bool> UsedBy)> Companions { get; } =
    [
        (Tailwind, UsesTailwind),
    ];

    /// <summary>A Tailwind project: a tailwind.config file, or Tailwind among package.json's dependencies (v4 has no config file).</summary>
    public static bool UsesTailwind(string root)
    {
        try
        {
            if (Directory.EnumerateFiles(root, "tailwind.config.*").Any())
            {
                return true;
            }

            var package = Path.Combine(root, "package.json");
            return File.Exists(package) && File.ReadAllText(package).Contains("\"tailwindcss\"", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The server for a language: a user override first, then the built-in default.</summary>
    public static LanguageServerDefinition? For(string languageId, IReadOnlyList<LanguageServerDefinition>? overrides = null) =>
        overrides?.FirstOrDefault(s => s.LanguageIds.Contains(languageId))
        ?? Defaults.FirstOrDefault(s => s.LanguageIds.Contains(languageId));

    /// <summary>
    /// The project root for a file: the nearest folder (walking up) that contains one of the server's
    /// markers, else the open workspace folder when the file is inside it, else the file's own folder.
    /// </summary>
    public static string FindRoot(LanguageServerDefinition server, string filePath, string? workspaceFolder)
    {
        var fileFolder = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        for (var folder = fileFolder; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            if (server.RootMarkers.Any(marker => ContainsMarker(folder, marker)))
            {
                return folder;
            }
        }

        if (workspaceFolder is not null)
        {
            var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceFolder));
            if (fileFolder.Equals(workspace, StringComparison.OrdinalIgnoreCase)
                || fileFolder.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return workspace;
            }
        }

        return fileFolder;
    }

    private static bool ContainsMarker(string folder, string marker)
    {
        try
        {
            return marker.Contains('*', StringComparison.Ordinal)
                ? Directory.EnumerateFiles(folder, marker).Any()
                : File.Exists(Path.Combine(folder, marker)) || Directory.Exists(Path.Combine(folder, marker));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
