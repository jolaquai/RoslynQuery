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
    public static string Resolve(string path) => Resolve(path, ResolveOptions.FromEnvironment(allowNuGetFallback: false));

    public static string Resolve(string path, ResolveOptions options)
    {
        var reference = AssemblyFacts.Read(path);
        if (reference is null) return null;
        if (!reference.IsReferenceAssembly) return path;

        return Candidates(path, reference, options).FirstOrDefault(candidate =>
        {
            var found = AssemblyFacts.Read(candidate);
            return found != null
                && !found.IsReferenceAssembly
                && string.Equals(found.Name, reference.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(found.PublicKeyToken, reference.PublicKeyToken, StringComparison.OrdinalIgnoreCase);
        });
    }

    public static bool IsReferenceAssembly(string path) => AssemblyFacts.Read(path)?.IsReferenceAssembly == true;

    /// <summary>
    /// The file that defines the type <paramref name="documentationId"/> belongs to, found by following type
    /// forwarders out of <paramref name="path"/>, or <paramref name="path"/> itself when it forwards nothing for it.
    /// ILSpy needs the defining file: handed a facade, it resolves a type only to the forwarder, which it cannot select.
    /// </summary>
    public static string DeclaringAssembly(string path, string documentationId)
    {
        var typeName = TypeNameOf(documentationId);
        if (typeName is null || string.IsNullOrEmpty(path)) return path;

        for (var hops = 0; hops < 8; hops++)
        {
            var target = ForwardedTo(path, typeName);
            if (target is null) return path;

            var next = Path.Combine(Path.GetDirectoryName(path), target + ".dll");
            if (!File.Exists(next) || string.Equals(next, path, StringComparison.OrdinalIgnoreCase)) return path;

            path = next;
        }

        return path;
    }

    /// <summary>The full name of the type a documentation id names or declares its member in; null for a namespace.</summary>
    private static string TypeNameOf(string documentationId)
    {
        if (string.IsNullOrEmpty(documentationId) || documentationId.Length < 3 || documentationId[1] != ':') return null;

        var body = documentationId.Substring(2);

        switch (documentationId[0])
        {
            case 'T':
                return body;
            case 'M':
            case 'P':
            case 'F':
            case 'E':
                var end = body.IndexOf('(');
                if (end < 0) end = body.IndexOf('~');
                if (end >= 0) body = body.Substring(0, end);

                var dot = body.LastIndexOf('.');
                return dot <= 0 ? null : body.Substring(0, dot);
            default:
                return null;
        }
    }

    /// <summary>
    /// The assembly a forwarder in <paramref name="path"/> sends the type to, or null. A nested type is forwarded along
    /// with its outermost type, so shorter prefixes of the name are tried until one is a forwarded top-level type.
    /// </summary>
    private static string ForwardedTo(string path, string typeName)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return null;

            var reader = pe.GetMetadataReader();
            var segments = typeName.Split('.');

            for (var i = segments.Length - 1; i >= 0; i--)
            {
                var name = segments[i];
                var ns = string.Join(".", segments, 0, i);

                foreach (var handle in reader.ExportedTypes)
                {
                    var exported = reader.GetExportedType(handle);

                    if (exported.IsForwarder
                        && exported.Implementation.Kind == HandleKind.AssemblyReference
                        && reader.StringComparer.Equals(exported.Name, name)
                        && reader.StringComparer.Equals(exported.Namespace, ns))
                        return reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)exported.Implementation).Name);
                }
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    /// <summary>Why an assembly from a reference pack found no implementation, or null when it is not from one.</summary>
    public static string ExplainUnresolved(string path, ResolveOptions options)
    {
        if (!ReferencePack.TryRead(path, out var pack)) return null;

        var installed = Subdirectories(pack.SharedRoot).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();

        return "It comes from the " + pack.Version + " reference pack, and no installed runtime qualifies under roll-forward policy "
            + (options.PolicyDescription ?? options.Policy.ToString())
            + (installed.Count == 0 ? "; no runtimes are installed." : "; installed: " + string.Join(", ", installed) + ".")
            + " Set " + RuntimeRollForward.PolicyVariable + " before starting Visual Studio to allow a different one."
            + (options.AllowNuGetFallback ? " No NuGet package matched either." : " Falling back to NuGet packages is off.");
    }

    private static IEnumerable<string> Candidates(string path, AssemblyFacts reference, ResolveOptions options)
    {
        if (ReferencePack.TryRead(path, out var pack)) return FromReferencePack(pack, options);
        if (TryNuGetReference(path, out var fromLib)) return fromLib;

        // A .NET reference assembly found outside its pack must never be matched to a .NET Framework namesake.
        if (reference.TargetFramework?.StartsWith(".NETCoreApp", StringComparison.OrdinalIgnoreCase) == true) return [];

        return GlobalAssemblyCache(reference).Concat(FrameworkDirectory(reference));
    }

    /// <summary>
    /// The shared runtime, exact version first and then the one the roll-forward policy picks; then, when allowed,
    /// the same assembly shipped as a NuGet package, found by the same two steps.
    /// </summary>
    private static IEnumerable<string> FromReferencePack(ReferencePack pack, ResolveOptions options)
    {
        var runtime = Versions(Subdirectories(pack.SharedRoot), pack.Version, options)
            .Select(version => Path.Combine(pack.SharedRoot, version, pack.FileName));

        return (options.AllowNuGetFallback ? runtime.Concat(NuGetPackage(pack, options)) : runtime).ToList();
    }

    private static IEnumerable<string> NuGetPackage(ReferencePack pack, ResolveOptions options)
    {
        if (string.IsNullOrEmpty(options.NuGetRoot)) return [];

        var package = Path.Combine(options.NuGetRoot, Path.GetFileNameWithoutExtension(pack.FileName).ToLowerInvariant());

        return Versions(Subdirectories(package), pack.Version, options)
            .SelectMany(version => LibFrameworks(Path.Combine(package, version, "lib"), pack.Framework)
                .Select(framework => Path.Combine(package, version, "lib", framework, pack.FileName)));
    }

    /// <summary>The exact version when it is there, then the one the roll-forward policy would bind to.</summary>
    private static IEnumerable<string> Versions(IReadOnlyList<string> available, string packVersion, ResolveOptions options)
    {
        var exact = available.FirstOrDefault(v => string.Equals(v, packVersion, StringComparison.OrdinalIgnoreCase));
        var rolled = RuntimeRollForward.Select(available, RuntimeRollForward.RequestFor(packVersion), options.Policy, options.RollToPrerelease);

        return new[] { exact, rolled }.Where(v => v != null).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The reference's own framework, then lower .NET frameworks newest first, then .NET Standard; never a .NET Framework build.</summary>
    private static IEnumerable<string> LibFrameworks(string lib, string framework)
    {
        var available = Subdirectories(lib);
        var ceiling = NetVersion(framework);

        var lower = available
            .Select(f => (Framework: f, Version: NetVersion(f)))
            .Where(f => f.Version != null && (ceiling == null || f.Version <= ceiling))
            .OrderByDescending(f => f.Version)
            .Select(f => f.Framework);

        var standard = available
            .Where(f => f.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase);

        return available.Where(f => string.Equals(f, framework, StringComparison.OrdinalIgnoreCase))
            .Concat(lower)
            .Concat(standard)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary><c>net9.0</c> and <c>netcoreapp3.1</c> as versions; null for .NET Framework (<c>net462</c>), .NET Standard and the rest.</summary>
    private static Version NetVersion(string framework)
    {
        string number = null;

        if (framework.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
            number = framework.Substring("netcoreapp".Length);
        else if (framework.StartsWith("net", StringComparison.OrdinalIgnoreCase) && !framework.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            number = framework.Substring("net".Length);

        return number != null && number.IndexOf('.') >= 0 && Version.TryParse(number, out var version) ? version : null;
    }

    private static IReadOnlyList<string> Subdirectories(string path)
    {
        try
        {
            return Directory.Exists(path) ? new DirectoryInfo(path).EnumerateDirectories().Select(d => d.Name).ToList() : [];
        }
        catch (Exception)
        {
            return [];
        }
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

    /// <summary><c>packs\X.Ref\ver\ref\tfm\a.dll</c>, or the same layout restored into the NuGet cache, with the runtime it maps to.</summary>
    private readonly struct ReferencePack
    {
        private ReferencePack(string sharedRoot, string version, string framework, string fileName)
        {
            SharedRoot = sharedRoot;
            Version = version;
            Framework = framework;
            FileName = fileName;
        }

        /// <summary><c>dotnet\shared\X</c>, holding one directory per installed runtime version.</summary>
        public string SharedRoot { get; }

        public string Version { get; }
        public string Framework { get; }
        public string FileName { get; }

        public static bool TryRead(string path, out ReferencePack pack)
        {
            pack = default;
            if (string.IsNullOrEmpty(path)) return false;

            var tfm = Directory.GetParent(path);
            var refDirectory = tfm?.Parent;
            var version = refDirectory?.Parent;
            var packDirectory = version?.Parent;

            if (packDirectory is null
                || !refDirectory.Name.Equals("ref", StringComparison.OrdinalIgnoreCase)
                || !packDirectory.Name.EndsWith(".Ref", StringComparison.OrdinalIgnoreCase))
                return false;

            var dotnet = packDirectory.Parent != null
                && packDirectory.Parent.Name.Equals("packs", StringComparison.OrdinalIgnoreCase)
                && packDirectory.Parent.Parent != null
                    ? packDirectory.Parent.Parent.FullName
                    : DotNetRoot();

            var shared = Path.Combine(dotnet, "shared", packDirectory.Name.Substring(0, packDirectory.Name.Length - ".Ref".Length));

            pack = new ReferencePack(shared, version.Name, tfm.Name, Path.GetFileName(path));
            return true;
        }
    }

    /// <summary><c>v4.0_4.0.0.0__b77a5c561934e089</c>.</summary>
    private static Version GacVersion(string directoryName)
    {
        var start = directoryName.IndexOf('_');
        var end = directoryName.IndexOf("__", StringComparison.Ordinal);
        if (start < 0 || end <= start) return null;

        return Version.TryParse(directoryName.Substring(start + 1, end - start - 1), out var version) ? version : null;
    }

    public const string DotNetRootVariable = "DOTNET_ROOT";

    private static string DotNetRoot() => DotNetRootFrom(Environment.GetEnvironmentVariable(DotNetRootVariable));

    /// <summary>Where the runtimes are looked up for a reference pack restored into the NuGet cache rather than under <c>dotnet\packs</c>.</summary>
    public static string DotNetRootFrom(string rawDotNetRoot) =>
        !string.IsNullOrEmpty(rawDotNetRoot) && Directory.Exists(rawDotNetRoot)
            ? rawDotNetRoot
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");

    public static string DescribeDotNetRoot(string rawDotNetRoot)
    {
        var root = DotNetRootFrom(rawDotNetRoot);

        return string.IsNullOrEmpty(rawDotNetRoot) ? root + " (" + DotNetRootVariable + " is not set)"
            : Directory.Exists(rawDotNetRoot) ? root + " (from " + DotNetRootVariable + ")"
            : root + " (" + DotNetRootVariable + "='" + rawDotNetRoot + "' does not exist)";
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
