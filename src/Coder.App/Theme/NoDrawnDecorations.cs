using Avalonia.Controls.Chrome;
using Avalonia.Controls.Templates;

namespace Coder.App.Theme;

/// <summary>Window decorations Avalonia would draw itself: none (Coder draws its own title bar).</summary>
internal sealed class NoDrawnDecorations : IWindowDrawnDecorationsTemplate
{
    public TemplateResult<WindowDrawnDecorationsContent> Build() => new(new WindowDrawnDecorationsContent(), new Avalonia.Controls.NameScope());

    object? Avalonia.Styling.ITemplate.Build() => Build();
}
