using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace RoslynQuery.Navigation;

/// <summary>
/// Maps a reference assembly to the implementation behind it. A project inside Visual Studio references
/// reference assemblies, whose members have no bodies, so decompiling the path it reports shows only stubs.
/// </summary>
internal static class ImplementationAssemblyResolver
{
    /// <summary>The path itself when it is already an implementation, otherwise the implementation, or null when none can be found.</summary>
    public static string Resolve(string path)
    {
        var reference = AssemblyFacts.Read(path);
        if (reference is null) return null;
        if (!reference.IsReferenceAssembly) return path;

        return Candidates(path, reference).FirstOrDefault(candidate =>
        {
            var found = AssemblyFacts.Read(candidate);
            return found != null
                && !found.IsReferenceAssembly
                && string.Equals(found.Name, reference.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(found.PublicKeyToken, reference.PublicKeyToken, StringComparison.OrdinalIgnoreCase);
        });
    }

    public static bool IsReferenceAssembly(string path) => AssemblyFacts.Read(path)?.IsReferenceAssembly == true;

    private static IEnumerable<string> Candidates(string path, AssemblyFacts reference)
    {
        if (TryReferencePack(path, out var fromPack)) return fromPack;
        if (TryNuGetReference(path, out var fromLib)) return fromLib;

        // A .NET reference assembly found outside its pack must never be matched to a .NET Framework namesake.
        if (reference.TargetFramework?.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase) == true) return [];

        return GlobalAssemblyCache(reference).Concat(FrameworkDirectory(reference));
    }

    /// <summary><c>packs\X.Ref\ver\ref\tfm\a.dll</c>, or the same layout restored into the NuGet cache, maps to <c>dotnet\shared\X\ver\a.dll</c>.</summary>
    private static bool TryReferencePack(string path, out IEnumerable<string> candidates)
    {
        candidates = null;

        var tfm = Directory.GetParent(path);
        var refDirectory = tfm?.Parent;
        var version = refDirectory?.Parent;
        var pack = version?.Parent;

        if (pack is null
            || !refDirectory.Name.Equals("ref", StringComparison.OrdinalIgnoreCase)
            || !pack.Name.EndsWith(".Ref", StringComparison.OrdinalIgnoreCase))
            return false;

        var dotnet = pack.Parent != null && pack.Parent.Name.Equals("packs", StringComparison.OrdinalIgnoreCase) && pack.Parent.Parent != null
            ? pack.Parent.Parent.FullName
            : DotNetRoot();

        var shared = Path.Combine(dotnet, "shared", pack.Name.Substring(0, pack.Name.Length - ".Ref".Length));
        var file = Path.GetFileName(path);

        candidates = RuntimeVersions(shared, version.Name).Select(v => Path.Combine(shared, v, file)).ToList();
        return true;
    }

    /// <summary><c>package\ver\ref\tfm\a.dll</c> maps to <c>package\ver\lib\tfm\a.dll</c>, then to any other <c>lib</c> framework carrying the file.</summary>
    private static bool TryNuGetReference(string path, out IEnumerable<string> candidates)
    {
        candidates = null;

        var tfm = Directory.GetParent(path);
        var refDirectory = tfm?.Parent;
        var version = refDirectory?.Parent;

        if (version is null || !refDirectory.Name.Equals("ref", StringComparison.OrdinalIgnoreCase)) return false;

        var lib = new DirectoryInfo(Path.Combine(version.FullName, "lib"));
        if (!lib.Exists) return false;

        var file = Path.GetFileName(path);

        candidates = new[] { Path.Combine(lib.FullName, tfm.Name, file) }
            .Concat(lib.EnumerateDirectories()
                .OrderByDescending(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => Path.Combine(d.FullName, file)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return true;
    }

    private static IEnumerable<string> GlobalAssemblyCache(AssemblyFacts reference)
    {
        if (string.IsNullOrEmpty(reference.PublicKeyToken)) yield break;

        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "assembly");
        var suffix = "__" + reference.PublicKeyToken;

        foreach (var cache in new[] { "GAC_MSIL", "GAC_64", "GAC_32" })
        {
            var named = new DirectoryInfo(Path.Combine(root, cache, reference.Name));
            if (!named.Exists) continue;

            var versions = named.EnumerateDirectories()
                .Where(d => d.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(d => (Directory: d, Version: GacVersion(d.Name)))
                .Where(v => v.Version != null)
                .OrderByDescending(v => v.Version == reference.Version)
                .ThenByDescending(v => v.Version)
                .ToList();

            foreach (var (directory, _) in versions)
                yield return Path.Combine(directory.FullName, reference.Name + ".dll");
        }
    }

    private static IEnumerable<string> FrameworkDirectory(AssemblyFacts reference)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        foreach (var framework in new[] { "Framework64", "Framework" })
            yield return Path.Combine(windows, "Microsoft.NET", framework, "v4.0.30319", reference.Name + ".dll");
    }

    /// <summary>Installed runtimes of the pack's major version: its own version first, then its minor, then newest.</summary>
    private static IEnumerable<string> RuntimeVersions(string sharedRoot, string packVersion)
    {
        if (!Directory.Exists(sharedRoot) || !Version.TryParse(Stable(packVersion), out var wanted)) return [];

        return new DirectoryInfo(sharedRoot).EnumerateDirectories()
            .Select(d => (d.Name, Version: Version.TryParse(Stable(d.Name), out var parsed) ? parsed : null))
            .Where(r => r.Version != null && r.Version.Major == wanted.Major)
            .OrderByDescending(r => string.Equals(r.Name, packVersion, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(r => r.Version.Minor == wanted.Minor)
            .ThenByDescending(r => r.Version)
            .Select(r => r.Name)
            .ToList();
    }

    private static string Stable(string version)
    {
        var dash = version.IndexOf('-');
        return dash < 0 ? version : version.Substring(0, dash);
    }

    /// <summary><c>v4.0_4.0.0.0__b77a5c561934e089</c>.</summary>
    private static Version GacVersion(string directoryName)
    {
        var start = directoryName.IndexOf('_');
        var end = directoryName.IndexOf("__", StringComparison.Ordinal);
        if (start < 0 || end <= start) return null;

        return Version.TryParse(directoryName.Substring(start + 1, end - start - 1), out var version) ? version : null;
    }

    private static string DotNetRoot()
    {
        var configured = Environment.GetEnvironmentVariable("DOTNET_ROOT");

        return !string.IsNullOrEmpty(configured) && Directory.Exists(configured)
            ? configured
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
    }

    private sealed class AssemblyFacts
    {
        public string Name;
        public Version Version;
        public string PublicKeyToken;
        public string TargetFramework;
        public bool IsReferenceAssembly;

        public static AssemblyFacts Read(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            try
            {
                using (var stream = File.OpenRead(path))
                using (var pe = new PEReader(stream))
                {
                    if (!pe.HasMetadata) return null;

                    var metadata = pe.GetMetadataReader();
                    if (!metadata.IsAssembly) return null;

                    var definition = metadata.GetAssemblyDefinition();
                    var facts = new AssemblyFacts
                    {
                        Name = metadata.GetString(definition.Name),
                        Version = definition.Version,
                        PublicKeyToken = TokenOf(metadata.GetBlobBytes(definition.PublicKey))
                    };

                    foreach (var handle in definition.GetCustomAttributes())
                    {
                        var attribute = metadata.GetCustomAttribute(handle);

                        switch (AttributeTypeName(metadata, attribute))
                        {
                            case "ReferenceAssemblyAttribute":
                                facts.IsReferenceAssembly = true;
                                break;
                            case "TargetFrameworkAttribute":
                                facts.TargetFramework = FirstStringArgument(metadata, attribute);
                                break;
                        }
                    }

                    return facts;
                }
            }
            catch (BadImageFormatException)
            {
                return null;
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        private static string AttributeTypeName(MetadataReader metadata, CustomAttribute attribute)
        {
            switch (attribute.Constructor.Kind)
            {
                case HandleKind.MemberReference:
                    var reference = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    return reference.Parent.Kind == HandleKind.TypeReference
                        ? metadata.GetString(metadata.GetTypeReference((TypeReferenceHandle)reference.Parent).Name)
                        : null;

                case HandleKind.MethodDefinition:
                    var method = metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                    return metadata.GetString(metadata.GetTypeDefinition(method.GetDeclaringType()).Name);

                default:
                    return null;
            }
        }

        private static string FirstStringArgument(MetadataReader metadata, CustomAttribute attribute)
        {
            var reader = metadata.GetBlobReader(attribute.Value);
            if (reader.Length < 2 || reader.ReadUInt16() != 1) return null;

            return reader.ReadSerializedString();
        }

        private static string TokenOf(byte[] publicKey)
        {
            if (publicKey is null || publicKey.Length == 0) return null;

            using (var sha1 = SHA1.Create())
            {
                var hash = sha1.ComputeHash(publicKey);
                var token = new char[16];

                for (var i = 0; i < 8; i++)
                {
                    var value = hash[hash.Length - 1 - i];
                    token[i * 2] = "0123456789abcdef"[value >> 4];
                    token[i * 2 + 1] = "0123456789abcdef"[value & 0xF];
                }

                return new string(token);
            }
        }
    }
}
