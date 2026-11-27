namespace Coder.Core.Emmet;

/// <summary>
/// One element (or text, or <c>(group)</c>) of a parsed abbreviation. The parser fills
/// <see cref="Repeat"/> and <see cref="IsGroup"/>; unrolling turns those into plain copies, so the tree
/// the expander writes holds only elements and text.
/// </summary>
internal sealed class EmmetNode
{
    /// <summary>The tag name; null for an implicit tag (<c>.item</c>) or a text-only node (<c>{hi}</c>).</summary>
    public string? Name { get; set; }

    public List<EmmetAttribute> Attributes { get; } = [];

    /// <summary>Text from <c>{…}</c>, in snippet syntax (it may hold <c>${1:placeholder}</c> fields).</summary>
    public string? Text { get; set; }

    public List<EmmetNode> Children { get; } = [];

    /// <summary>Written as <c>&lt;br&gt;</c>, without content or closing tag (<c>br/</c>, or a void element).</summary>
    public bool SelfClosing { get; set; }

    public int Repeat { get; set; } = 1;

    public bool IsGroup { get; set; }

    public bool IsText => Name is null && Attributes.Count == 0 && Children.Count == 0 && Text is not null;

    public EmmetNode Clone()
    {
        var copy = new EmmetNode { Name = Name, Text = Text, SelfClosing = SelfClosing, Repeat = Repeat, IsGroup = IsGroup };
        copy.Attributes.AddRange(Attributes.Select(a => a with { }));
        copy.Children.AddRange(Children.Select(c => c.Clone()));
        return copy;
    }

    /// <summary>Adds or merges an attribute: classes join, a value replaces, an empty one keeps what is there.</summary>
    public void SetAttribute(EmmetAttribute attribute)
    {
        var index = Attributes.FindIndex(a => a.Name == attribute.Name);
        if (index < 0)
        {
            Attributes.Add(attribute);
        }
        else if (attribute.Name == "class" && Attributes[index].Value is { Length: > 0 } existing && attribute.Value is { Length: > 0 } added)
        {
            Attributes[index] = Attributes[index] with { Value = existing + " " + added };
        }
        else if (attribute.Value is not null || attribute.Boolean)
        {
            Attributes[index] = attribute;
        }
    }
}

/// <summary>
/// An attribute: <c>[href]</c> has no value (it becomes a tab stop), <c>[disabled.]</c> is boolean
/// (written without a value).
/// </summary>
internal sealed record EmmetAttribute(string Name, string? Value, bool Boolean = false);
