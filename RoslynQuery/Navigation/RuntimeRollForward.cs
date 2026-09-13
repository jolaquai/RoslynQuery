using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace RoslynQuery.Navigation;

/// <summary>The .NET host's roll-forward policies. <see cref="Minor"/> is first so <c>default</c> is the host's own default.</summary>
internal enum RollForwardPolicy
{
    Minor,
    Major,
    LatestPatch,
    LatestMinor,
    LatestMajor,
    Disable
}

/// <summary>
/// Picks the version the .NET host would bind a framework-dependent app to, following the documented
/// roll-forward rules, including preferring a release over a prerelease.
/// </summary>
internal static class RuntimeRollForward
{
    public const string PolicyVariable = "DOTNET_ROLL_FORWARD";
    public const string PrereleaseVariable = "DOTNET_ROLL_FORWARD_TO_PRERELEASE";

    public static bool CurrentRollsToPrerelease =>
        string.Equals(Environment.GetEnvironmentVariable(PrereleaseVariable)?.Trim(), "1", StringComparison.Ordinal);

    public static bool TryParse(string value, out RollForwardPolicy policy)
    {
        policy = RollForwardPolicy.Minor;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var text = value.Trim();
        foreach (RollForwardPolicy candidate in Enum.GetValues(typeof(RollForwardPolicy)))
        {
            if (string.Equals(candidate.ToString(), text, StringComparison.OrdinalIgnoreCase))
            {
                policy = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>What an app built on a targeting pack asks the host for: its major.minor, or a prerelease pack exactly.</summary>
    public static string RequestFor(string packVersion)
    {
        if (!RuntimeVersion.TryParse(packVersion, out var version)) return packVersion;

        return version.IsPrerelease
            ? packVersion
            : version.Major.ToString(CultureInfo.InvariantCulture) + "." + version.Minor.ToString(CultureInfo.InvariantCulture) + ".0";
    }

    /// <summary>The entry of <paramref name="available"/> the host would bind <paramref name="requested"/> to, or null.</summary>
    public static string Select(IEnumerable<string> available, string requested, RollForwardPolicy policy, bool rollToPrerelease)
    {
        if (!RuntimeVersion.TryParse(requested, out var wanted)) return null;

        // Never backwards: a version below the request is not a candidate under any policy.
        var candidates = new List<Candidate>();
        foreach (var name in available)
            if (RuntimeVersion.TryParse(name, out var version) && version.CompareTo(wanted) >= 0)
                candidates.Add(new Candidate(name, version));

        if (policy == RollForwardPolicy.Disable)
            return candidates.FirstOrDefault(c => c.Version.CompareTo(wanted) == 0)?.Name;

        if (!wanted.IsPrerelease && !rollToPrerelease)
        {
            var release = Pick(candidates.Where(c => !c.Version.IsPrerelease).ToList(), wanted, policy);
            if (release != null) return release;
        }

        return Pick(candidates, wanted, policy);
    }

    private static string Pick(List<Candidate> candidates, RuntimeVersion wanted, RollForwardPolicy policy)
    {
        if (candidates.Count == 0) return null;

        (int Major, int Minor)? band;
        switch (policy)
        {
            case RollForwardPolicy.LatestPatch:
                band = Band(candidates, wanted.Major, wanted.Minor);
                break;
            case RollForwardPolicy.Minor:
                band = Band(candidates, wanted.Major, wanted.Minor) ?? LowestMinorAbove(candidates, wanted);
                break;
            case RollForwardPolicy.Major:
                band = candidates.Any(c => c.Version.Major == wanted.Major)
                    ? Band(candidates, wanted.Major, wanted.Minor) ?? LowestMinorAbove(candidates, wanted)
                    : LowestMajorAbove(candidates, wanted);
                break;
            case RollForwardPolicy.LatestMinor:
                band = Highest(candidates.Where(c => c.Version.Major == wanted.Major).ToList());
                break;
            case RollForwardPolicy.LatestMajor:
                band = Highest(candidates);
                break;
            default:
                return null;
        }

        if (band is null) return null;

        var inBand = candidates.Where(c => c.Version.Major == band.Value.Major && c.Version.Minor == band.Value.Minor).ToList();
        var releases = inBand.Where(c => !c.Version.IsPrerelease).ToList();

        // A release rolls to its latest patch; a prerelease does not.
        return releases.Count > 0
            ? releases.OrderByDescending(c => c.Version).First().Name
            : inBand.OrderBy(c => c.Version).First().Name;
    }

    private static (int Major, int Minor)? Band(List<Candidate> candidates, int major, int minor) =>
        candidates.Any(c => c.Version.Major == major && c.Version.Minor == minor) ? (major, minor) : null;

    private static (int Major, int Minor)? LowestMinorAbove(List<Candidate> candidates, RuntimeVersion wanted)
    {
        var minors = candidates.Where(c => c.Version.Major == wanted.Major && c.Version.Minor > wanted.Minor).Select(c => c.Version.Minor).ToList();
        return minors.Count == 0 ? null : (wanted.Major, minors.Min());
    }

    private static (int Major, int Minor)? LowestMajorAbove(List<Candidate> candidates, RuntimeVersion wanted)
    {
        var higher = candidates.Where(c => c.Version.Major > wanted.Major).ToList();
        if (higher.Count == 0) return null;

        var major = higher.Min(c => c.Version.Major);
        return (major, higher.Where(c => c.Version.Major == major).Min(c => c.Version.Minor));
    }

    private static (int Major, int Minor)? Highest(List<Candidate> candidates)
    {
        if (candidates.Count == 0) return null;

        var major = candidates.Max(c => c.Version.Major);
        return (major, candidates.Where(c => c.Version.Major == major).Max(c => c.Version.Minor));
    }

    private sealed class Candidate(string name, RuntimeVersion version)
    {
        public string Name { get; } = name;
        public RuntimeVersion Version { get; } = version;
    }

    /// <summary><c>major.minor.patch</c> with an optional prerelease label, ordered the SemVer way.</summary>
    private readonly struct RuntimeVersion : IComparable<RuntimeVersion>
    {
        private RuntimeVersion(int major, int minor, int patch, string label)
        {
            Major = major;
            Minor = minor;
            Patch = patch;
            Label = label;
        }

        public int Major { get; }
        public int Minor { get; }
        public int Patch { get; }
        public string Label { get; }

        public bool IsPrerelease => Label != null;

        public static bool TryParse(string text, out RuntimeVersion version)
        {
            version = default;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var dash = text.IndexOf('-');
            var parts = (dash < 0 ? text : text.Substring(0, dash)).Split('.');
            if (parts.Length != 3) return false;

            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor)
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
                return false;

            var label = dash < 0 ? null : text.Substring(dash + 1);
            if (label != null && label.Length == 0) return false;

            version = new RuntimeVersion(major, minor, patch, label);
            return true;
        }

        public int CompareTo(RuntimeVersion other)
        {
            var result = Major.CompareTo(other.Major);
            if (result != 0) return result;

            result = Minor.CompareTo(other.Minor);
            if (result != 0) return result;

            result = Patch.CompareTo(other.Patch);
            if (result != 0) return result;

            if (Label is null) return other.Label is null ? 0 : 1;
            if (other.Label is null) return -1;

            return CompareLabels(Label, other.Label);
        }

        private static int CompareLabels(string left, string right)
        {
            var a = left.Split('.');
            var b = right.Split('.');

            for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            {
                var aNumeric = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out var aNumber);
                var bNumeric = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out var bNumber);

                var result = aNumeric && bNumeric ? aNumber.CompareTo(bNumber)
                    : aNumeric ? -1
                    : bNumeric ? 1
                    : string.CompareOrdinal(a[i], b[i]);

                if (result != 0) return result;
            }

            return a.Length.CompareTo(b.Length);
        }
    }
}
