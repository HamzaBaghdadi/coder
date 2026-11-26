using static Coder.Core.Syntax.LanguageDefinition;

namespace Coder.Core.Syntax;

/// <summary>Built-in language definitions and lookup by file name.</summary>
public static class Languages
{
    public static LanguageDefinition PlainText { get; } = new()
    {
        Id = "plaintext",
        DisplayName = "Plain Text",
        FileExtensions = [".txt", ".log"],
        StringQuotes = "",
        Brackets = "",
    };

    public static IReadOnlyList<LanguageDefinition> All { get; } =
    [
        new()
        {
            Id = "csharp", DisplayName = "C#", FileExtensions = [".cs", ".csx"], IconColor = "#A78BFA",
            LineComment = "//", BlockComment = ("/*", "*/"), MultiLineStringDelimiter = "\"\"\"",
            HashLinesAreDirectives = true, PascalCaseIsType = true,
            Keywords = Words("abstract as async await base break case catch checked class const continue default delegate do else enum event explicit extern finally fixed for foreach get goto if implicit in init interface internal is lock namespace new operator out override params private protected public readonly record ref required return sealed set sizeof stackalloc static struct switch this throw try typeof unchecked unsafe using value var virtual volatile when where while with yield file scoped partial global nameof managed"),
            Types = Words("bool byte char decimal double float int long nint nuint object sbyte short string uint ulong ushort void dynamic"),
            Constants = Words("true false null"),
        },
        new()
        {
            Id = "javascript", DisplayName = "JavaScript", FileExtensions = [".js", ".mjs", ".cjs", ".jsx"], IconColor = "#FACC15",
            LineComment = "//", BlockComment = ("/*", "*/"), MultiLineStringDelimiter = "`", ExtraIdentifierChars = "_$",
            PascalCaseIsType = true,
            Keywords = Words("async await break case catch class const continue debugger default delete do else export extends finally for from function get if import in instanceof let new of return set static super switch this throw try typeof var void while with yield"),
            Constants = Words("true false null undefined NaN Infinity"),
        },
        new()
        {
            Id = "typescript", DisplayName = "TypeScript", FileExtensions = [".ts", ".tsx", ".mts", ".cts"], IconColor = "#60A5FA",
            LineComment = "//", BlockComment = ("/*", "*/"), MultiLineStringDelimiter = "`", ExtraIdentifierChars = "_$",
            PascalCaseIsType = true,
            Keywords = Words("abstract as asserts async await break case catch class const continue declare default delete do else enum export extends finally for from function get if implements import in infer instanceof interface is keyof let module namespace new of override private protected public readonly return satisfies set static super switch this throw try type typeof var void while with yield"),
            Types = Words("any boolean never number object string symbol unknown bigint"),
            Constants = Words("true false null undefined NaN Infinity"),
        },
        new()
        {
            Id = "json", DisplayName = "JSON", FileExtensions = [".json", ".jsonc", ".code-workspace"], FileNames = [".prettierrc", ".babelrc"],
            IconColor = "#FBBF24", LineComment = "//", BlockComment = ("/*", "*/"), StringQuotes = "\"",
            Constants = Words("true false null"),
        },
        new()
        {
            Id = "python", DisplayName = "Python", FileExtensions = [".py", ".pyw", ".pyi"], IconColor = "#38BDF8",
            LineComment = "#", MultiLineStringDelimiter = "\"\"\"", PascalCaseIsType = true,
            Keywords = Words("and as assert async await break class continue def del elif else except finally for from global if import in is lambda match case nonlocal not or pass raise return try while with yield self"),
            Types = Words("int float str bool list dict set tuple bytes object type"),
            Constants = Words("True False None"),
        },
        new()
        {
            Id = "java", DisplayName = "Java", FileExtensions = [".java"], IconColor = "#F87171",
            LineComment = "//", BlockComment = ("/*", "*/"), MultiLineStringDelimiter = "\"\"\"", PascalCaseIsType = true,
            Keywords = Words("abstract assert break case catch class const continue default do else enum extends final finally for goto if implements import instanceof interface native new package private protected public record return sealed permits static strictfp super switch synchronized this throw throws transient try var void volatile while yield"),
            Types = Words("boolean byte char double float int long short"),
            Constants = Words("true false null"),
        },
        new()
        {
            Id = "kotlin", DisplayName = "Kotlin", FileExtensions = [".kt", ".kts"], IconColor = "#C084FC",
            LineComment = "//", BlockComment = ("/*", "*/"), MultiLineStringDelimiter = "\"\"\"", PascalCaseIsType = true,
            Keywords = Words("as break class continue do else for fun if in interface is object package return super this throw try typealias typeof val var when while by catch constructor data enum finally get import init internal lateinit open override private protected public sealed set suspend companion inline"),
            Constants = Words("true false null"),
        },
        new()
        {
            Id = "c", DisplayName = "C", FileExtensions = [".c", ".h"], IconColor = "#93C5FD",
            LineComment = "//", BlockComment = ("/*", "*/"), HashLinesAreDirectives = true,
            Keywords = Words("auto break case const continue default do else enum extern for goto if inline register restrict return sizeof static struct switch typedef union volatile while"),
            Types = Words("char double float int long short signed unsigned void bool size_t int8_t int16_t int32_t int64_t uint8_t uint16_t uint32_t uint64_t"),
            Constants = Words("true false NULL"),
        },
        new()
        {
            Id = "cpp", DisplayName = "C++", FileExtensions = [".cpp", ".cc", ".cxx", ".hpp", ".hh", ".hxx"], IconColor = "#818CF8",
            LineComment = "//", BlockComment = ("/*", "*/"), HashLinesAreDirectives = true, PascalCaseIsType = true,
            Keywords = Words("alignas alignof auto break case catch class const consteval constexpr constinit continue co_await co_return co_yield decltype default delete do else enum explicit export extern for friend goto if inline mutable namespace new noexcept operator private protected public register requires return sizeof static static_assert static_cast dynamic_cast reinterpret_cast const_cast struct switch template this thread_local throw try typedef typeid typename union using virtual volatile while concept override final"),
            Types = Words("bool char char8_t char16_t char32_t double float int long short signed unsigned void wchar_t size_t"),
            Constants = Words("true false nullptr NULL"),
        },
        new()
        {
            Id = "rust", DisplayName = "Rust", FileExtensions = [".rs"], IconColor = "#FB923C",
            LineComment = "//", BlockComment = ("/*", "*/"), PascalCaseIsType = true, StringQuotes = "\"",
            Keywords = Words("as async await break const continue crate dyn else enum extern fn for if impl in let loop match mod move mut pub ref return self Self static struct super trait type unsafe use where while"),
            Types = Words("bool char f32 f64 i8 i16 i32 i64 i128 isize str u8 u16 u32 u64 u128 usize String Vec Option Result Box"),
            Constants = Words("true false None Some Ok Err"),
        },
        new()
        {
            Id = "go", DisplayName = "Go", FileExtensions = [".go"], IconColor = "#22D3EE",
            LineComment = "//", BlockComment = ("/*", "*/"), MultiLineStringDelimiter = "`",
            Keywords = Words("break case chan const continue default defer else fallthrough for func go goto if import interface map package range return select struct switch type var"),
            Types = Words("bool byte complex64 complex128 error float32 float64 int int8 int16 int32 int64 rune string uint uint8 uint16 uint32 uint64 uintptr any"),
            Constants = Words("true false nil iota"),
        },
        new()
        {
            Id = "swift", DisplayName = "Swift", FileExtensions = [".swift"], IconColor = "#F97316",
            LineComment = "//", BlockComment = ("/*", "*/"), MultiLineStringDelimiter = "\"\"\"", PascalCaseIsType = true, StringQuotes = "\"",
            Keywords = Words("associatedtype class deinit enum extension fileprivate func import init inout internal let open operator private protocol public rethrows static struct subscript typealias var break case continue default defer do else fallthrough for guard if in repeat return switch where while as catch is super self Self throw throws try async await some any"),
            Constants = Words("true false nil"),
        },
        new()
        {
            Id = "dart", DisplayName = "Dart", FileExtensions = [".dart"], IconColor = "#2DD4BF",
            LineComment = "//", BlockComment = ("/*", "*/"), MultiLineStringDelimiter = "'''", PascalCaseIsType = true,
            Keywords = Words("abstract as assert async await break case catch class const continue covariant default deferred do dynamic else enum export extends extension external factory final finally for get if implements import in interface is late library mixin new on operator part required rethrow return sealed set show static super switch sync this throw try typedef var void when while with yield"),
            Types = Words("int double num bool String List Map Set Future Stream Object"),
            Constants = Words("true false null"),
        },
        new()
        {
            Id = "php", DisplayName = "PHP", FileExtensions = [".php"], IconColor = "#A5B4FC",
            LineComment = "//", BlockComment = ("/*", "*/"), ExtraIdentifierChars = "_$", PascalCaseIsType = true,
            Keywords = Words("abstract and array as break callable case catch class clone const continue declare default do echo else elseif empty enddeclare endfor endforeach endif endswitch endwhile enum extends final finally fn for foreach function global goto if implements include include_once instanceof insteadof interface isset list match namespace new or print private protected public readonly require require_once return static switch throw trait try unset use var while xor yield"),
            Constants = Words("true false null TRUE FALSE NULL", ignoreCase: true),
        },
        new()
        {
            Id = "ruby", DisplayName = "Ruby", FileExtensions = [".rb"], FileNames = ["Gemfile", "Rakefile"], IconColor = "#F43F5E",
            LineComment = "#", PascalCaseIsType = true, ExtraIdentifierChars = "_?!",
            Keywords = Words("alias and begin break case class def defined? do else elsif end ensure for if in module next not or redo rescue retry return self super then undef unless until when while yield require attr_accessor attr_reader"),
            Constants = Words("true false nil"),
        },
        new()
        {
            Id = "lua", DisplayName = "Lua", FileExtensions = [".lua"], IconColor = "#60A5FA",
            LineComment = "--", BlockComment = ("--[[", "]]"),
            Keywords = Words("and break do else elseif end for function goto if in local not or repeat return then until while"),
            Constants = Words("true false nil"),
        },
        new()
        {
            Id = "shellscript", DisplayName = "Shell", FileExtensions = [".sh", ".bash", ".zsh"], FileNames = [".bashrc", ".zshrc"], IconColor = "#4ADE80",
            LineComment = "#", ExtraIdentifierChars = "_-",
            Keywords = Words("if then else elif fi case esac for select while until do done in function return local export readonly declare unset source exit echo"),
            Constants = Words("true false"),
        },
        new()
        {
            Id = "powershell", DisplayName = "PowerShell", FileExtensions = [".ps1", ".psm1", ".psd1"], IconColor = "#38BDF8",
            LineComment = "#", BlockComment = ("<#", "#>"), ExtraIdentifierChars = "_-$",
            Keywords = Words("begin break catch class continue data do dynamicparam else elseif end enum exit filter finally for foreach from function if in param process return switch throw trap try until using var while", ignoreCase: true),
            Constants = Words("$true $false $null", ignoreCase: true),
        },
        new()
        {
            Id = "sql", DisplayName = "SQL", FileExtensions = [".sql"], IconColor = "#FCD34D",
            LineComment = "--", BlockComment = ("/*", "*/"), StringQuotes = "'",
            Keywords = Words("select from where insert into values update set delete create alter drop table view index join inner left right outer full on group by order having limit offset union all distinct as and or not in is like between exists case when then else end primary key foreign references default constraint begin commit rollback transaction with returning", ignoreCase: true),
            Types = Words("int integer bigint smallint decimal numeric float real double varchar char text date time timestamp boolean bool uuid json jsonb", ignoreCase: true),
            Constants = Words("null true false", ignoreCase: true),
        },
        new()
        {
            Id = "css", DisplayName = "CSS", FileExtensions = [".css", ".scss", ".less"], IconColor = "#60A5FA",
            LineComment = "//", BlockComment = ("/*", "*/"), ExtraIdentifierChars = "_-@",
            Keywords = Words("@media @import @keyframes @font-face @supports @layer @container"),
            Constants = Words("inherit initial unset auto none"),
        },
        new()
        {
            Id = "html", DisplayName = "HTML", FileExtensions = [".html", ".htm", ".xhtml"], IconColor = "#FB923C",
            BlockComment = ("<!--", "-->"), ExtraIdentifierChars = "_-", Brackets = "<>", MarkupTags = true,
        },
        new()
        {
            Id = "xml", DisplayName = "XML", FileExtensions = [".xml", ".csproj", ".props", ".targets", ".axaml", ".xaml", ".svg", ".resx", ".config", ".plist", ".manifest"],
            IconColor = "#FDBA74", BlockComment = ("<!--", "-->"), ExtraIdentifierChars = "_-:", Brackets = "<>", MarkupTags = true,
        },
        new()
        {
            Id = "yaml", DisplayName = "YAML", FileExtensions = [".yml", ".yaml"], IconColor = "#F87171",
            LineComment = "#", ExtraIdentifierChars = "_-",
            Constants = Words("true false null yes no on off ~", ignoreCase: true),
        },
        new()
        {
            Id = "toml", DisplayName = "TOML", FileExtensions = [".toml", ".ini", ".cfg", ".editorconfig"], FileNames = [".editorconfig", ".gitconfig"],
            IconColor = "#9B9B9B", LineComment = "#", MultiLineStringDelimiter = "\"\"\"", ExtraIdentifierChars = "_-",
            Constants = Words("true false"),
        },
        new()
        {
            Id = "dockerfile", DisplayName = "Dockerfile", FileNames = ["Dockerfile", "Containerfile"], FileExtensions = [".dockerfile"], IconColor = "#38BDF8",
            LineComment = "#",
            Keywords = Words("from run cmd label maintainer expose env add copy entrypoint volume user workdir arg onbuild stopsignal healthcheck shell as", ignoreCase: true),
        },
        new()
        {
            Id = "markdown", DisplayName = "Markdown", FileExtensions = [".md", ".markdown", ".mdx"], IconColor = "#7DD3FC",
            StringQuotes = "`", Brackets = "()[]", HashLinesAreDirectives = true,
        },
        PlainText,
    ];

    public static LanguageDefinition FromFileName(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        foreach (var language in All)
        {
            if (language.FileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return language;
            }
        }

        foreach (var language in All)
        {
            if (language.FileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return language;
            }
        }

        return PlainText;
    }
}
