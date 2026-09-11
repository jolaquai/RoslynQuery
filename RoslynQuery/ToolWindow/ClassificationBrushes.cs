using System;
using System.Collections.Generic;
using System.Windows.Media;

using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;

namespace RoslynQuery.ToolWindow;

/// <summary>Editor foreground brush per classification name; null until initialised or when the editor has no colour for it.</summary>
internal static class ClassificationBrushes
{
    private static readonly Dictionary<string, Brush> Cache = new Dictionary<string, Brush>(StringComparer.Ordinal);

    private static IClassificationFormatMap _formatMap;
    private static IClassificationTypeRegistryService _registry;

    /// <summary>Raised after a theme or font-and-colors change has invalidated every cached brush.</summary>
    public static event EventHandler Changed;

    public static void Initialize(IComponentModel componentModel)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        if (_formatMap != null || componentModel is null) return;

        _registry = componentModel.GetService<IClassificationTypeRegistryService>();
        _formatMap = componentModel.GetService<IClassificationFormatMapService>()?.GetClassificationFormatMap("text");
        if (_formatMap is null) return;

        _formatMap.ClassificationFormatMappingChanged += OnMappingChanged;
    }

    public static Brush For(string classificationName)
    {
        if (_formatMap is null || _registry is null || classificationName is null) return null;

        ThreadHelper.ThrowIfNotOnUIThread();

        if (Cache.TryGetValue(classificationName, out var cached)) return cached;

        var type = _registry.GetClassificationType(classificationName);
        var properties = type is null ? null : _formatMap.GetTextProperties(type);
        var brush = properties is null || properties.ForegroundBrushEmpty ? null : properties.ForegroundBrush;

        Cache[classificationName] = brush;
        return brush;
    }

    private static void OnMappingChanged(object sender, EventArgs e)
    {
        Cache.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
