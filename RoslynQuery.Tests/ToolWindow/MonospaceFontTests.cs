using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

using RoslynQuery.ToolWindow;

using Xunit;

namespace RoslynQuery.Tests;

public class MonospaceFontTests
{
    [Fact]
    public void TheChain_ResolvesToAnInstalledFont()
    {
        var typeface = new Typeface(MonospaceFont.Family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        Assert.True(typeface.TryGetGlyphTypeface(out _));
    }

    [Fact]
    public void TheChain_EndsInWpfsOwnMonospaceFallback() =>
        Assert.EndsWith("Global Monospace", MonospaceFont.Chain);

    /// <summary>One definition: a literal font name anywhere else would quietly opt that control out of the chain.</summary>
    [Fact]
    public void NoMonospaceFontIsHardcodedOutsideTheChain()
    {
        var source = Path.Combine(RepositoryFiles.Root, "RoslynQuery");
        var literal = new Regex(@"\b(Consolas|Courier New|Cascadia (Mono|Code)|Aptos Mono|Global Monospace)\b");

        var offenders = Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains(@"\obj\") && !f.Contains(@"\bin\"))
            .Where(f => !Path.GetFileName(f).Equals("MonospaceFont.cs", StringComparison.OrdinalIgnoreCase))
            .Where(f => literal.IsMatch(File.ReadAllText(f)))
            .Select(f => f.Substring(source.Length + 1))
            .ToList();

        Assert.Empty(offenders);
    }
}
