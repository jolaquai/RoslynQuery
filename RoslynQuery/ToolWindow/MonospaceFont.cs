using System.Windows.Media;

namespace RoslynQuery.ToolWindow;

/// <summary>
/// The one monospace font every predicate and signature is shown in. WPF walks the list per glyph;
/// "Global Monospace" is WPF's own composite fallback, its equivalent of CSS <c>monospace</c>.
/// </summary>
public static class MonospaceFont
{
    public const string Chain = "Aptos Mono, Consolas, Global Monospace";

    public static FontFamily Family { get; } = new FontFamily(Chain);
}
