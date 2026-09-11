using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace RoslynQuery.Navigation;

/// <summary>
/// Decompiles the type declaring a metadata symbol and positions on the symbol. Only implementation assemblies
/// are decompiled: a reference assembly is resolved first and refused when nothing stands behind it.
/// </summary>
internal static class DecompiledSourceProvider
{
    private static readonly object Gate = new object();

    internal static int DecompilersCreated { get; private set; }

    // NoInlining keeps ICSharpCode.Decompiler out of the caller's JIT, so a missing assembly throws inside the caller's try.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static DecompiledSource Decompile(string assemblyPath, string documentationId)
    {
        if (string.IsNullOrEmpty(documentationId)) return DecompiledSource.Failed("This row has no documentation id to look it up by.");

        var implementation = ImplementationAssemblyResolver.Resolve(assemblyPath);
        if (implementation is null)
        {
            return DecompiledSource.Failed(ImplementationAssemblyResolver.IsReferenceAssembly(assemblyPath)
                ? $"{Path.GetFileName(assemblyPath)} is a reference assembly and no implementation was found for it, so there are no method bodies to show."
                : $"{assemblyPath} could not be read as an assembly.");
        }

        lock (Gate)
        {
            return DecompileFrom(implementation, documentationId, forwards: 0);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DecompiledSource DecompileFrom(string path, string documentationId, int forwards)
    {
        var decompiler = DecompilerFor(path);
        var entity = IdStringProvider.FindEntity(documentationId, new SimpleTypeResolveContext(decompiler.TypeSystem.MainModule));
        if (entity is null) return DecompiledSource.Failed($"{documentationId} was not found in {Path.GetFileName(path)}.");

        if (!entity.ParentModule.IsMainModule) return FollowForward(entity, path, documentationId, forwards);

        var top = entity as ITypeDefinition ?? entity.DeclaringTypeDefinition;
        while (top?.DeclaringTypeDefinition != null) top = top.DeclaringTypeDefinition;
        if (top is null) return DecompiledSource.Failed($"{documentationId} has no declaring type to decompile.");

        var tree = decompiler.DecompileType(top.FullTypeName);
        var writer = new StringWriter();
        tree.AcceptVisitor(new CSharpOutputVisitor(TokenWriter.CreateWriterThatSetsLocationsInAST(writer, "\t"), Cache.Settings.CSharpFormattingOptions));

        var (line, column) = LocationOf(tree, entity);
        var module = decompiler.TypeSystem.MainModule;

        return DecompiledSource.Found(writer.ToString(), line, column, module.AssemblyName, module.AssemblyVersion, top.ReflectionName);
    }

    /// <summary>A facade forwards the type to the assembly that declares it.</summary>
    private static DecompiledSource FollowForward(IEntity entity, string path, string documentationId, int forwards)
    {
        var implementation = ImplementationAssemblyResolver.Resolve(entity.ParentModule.MetadataFile?.FileName);

        if (forwards >= 3 || implementation is null || string.Equals(implementation, path, StringComparison.OrdinalIgnoreCase))
        {
            return DecompiledSource.Failed(
                $"{documentationId} is forwarded from {Path.GetFileName(path)} to {entity.ParentModule.AssemblyName}, whose implementation could not be found.");
        }

        return DecompileFrom(implementation, documentationId, forwards + 1);
    }

    /// <summary>Read off the syntax tree, which also positions declarations without a name token, such as indexers and operators.</summary>
    private static (int Line, int Column) LocationOf(SyntaxTree tree, IEntity entity)
    {
        var target = entity is IMember member ? member.MemberDefinition : entity;

        var declaration = tree.Descendants.OfType<EntityDeclaration>().FirstOrDefault(d =>
            d.GetSymbol() is IEntity declared
            && declared.MetadataToken == target.MetadataToken
            && declared.ParentModule == target.ParentModule);

        if (declaration is null) return (0, 0);

        var name = declaration.NameToken;

        // A declaration's own StartLocation is unreliable (an event reports line 0); its first code child is not.
        var at = name != null && !name.IsNull && name.StartLocation.Line > 0
            ? name.StartLocation
            : declaration.Children.FirstOrDefault(c => !(c is AttributeSection) && !(c is Comment))?.StartLocation ?? declaration.StartLocation;

        return at.Line > 0 ? (at.Line - 1, Math.Max(0, at.Column - 1)) : (0, 0);
    }

    private static CSharpDecompiler DecompilerFor(string path)
    {
        if (Cache.Decompilers.TryGetValue(path, out var existing)) return existing;

        var file = new PEFile(path);
        var decompiler = new CSharpDecompiler(file, new UniversalAssemblyResolver(path, false, file.DetectTargetFrameworkId()), Cache.Settings);

        Cache.Decompilers[path] = decompiler;
        DecompilersCreated++;

        return decompiler;
    }

    private static class Cache
    {
        public static readonly Dictionary<string, CSharpDecompiler> Decompilers = new Dictionary<string, CSharpDecompiler>(StringComparer.OrdinalIgnoreCase);

        public static readonly DecompilerSettings Settings = new DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false };
    }
}
