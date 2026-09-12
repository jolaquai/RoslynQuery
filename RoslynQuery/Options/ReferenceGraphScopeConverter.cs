using System;
using System.ComponentModel;
using System.Globalization;

using RoslynQuery.Query;

namespace RoslynQuery.Options;

/// <summary>
/// Offers the options grid exactly the three scopes the Reference Graph window has, worded the way its own
/// combo words them. <see cref="ScopeKind"/> also carries the two scopes only the query window uses, and
/// offering those here would let the default name a scope the combo cannot select.
/// </summary>
internal sealed class ReferenceGraphScopeConverter : EnumConverter
{
    private static readonly (ScopeKind Scope, string Text)[] Supported =
    [
        (ScopeKind.Document, "Current document"),
        (ScopeKind.Project, "Current project"),
        (ScopeKind.Solution, "My solution")
    ];

    public ReferenceGraphScopeConverter() : base(typeof(ScopeKind))
    {
    }

    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext context) =>
        new StandardValuesCollection(Array.ConvertAll(Supported, s => (object)s.Scope));

    public override bool GetStandardValuesExclusive(ITypeDescriptorContext context) => true;

    public override bool GetStandardValuesSupported(ITypeDescriptorContext context) => true;

    public override object ConvertTo(ITypeDescriptorContext context, CultureInfo culture, object value, Type destinationType)
    {
        if (destinationType == typeof(string) && value is ScopeKind scope)
            foreach (var supported in Supported)
                if (supported.Scope == scope)
                    return supported.Text;

        return base.ConvertTo(context, culture, value, destinationType);
    }

    public override object ConvertFrom(ITypeDescriptorContext context, CultureInfo culture, object value)
    {
        if (value is string text)
            foreach (var supported in Supported)
                if (string.Equals(text, supported.Text, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(text, supported.Scope.ToString(), StringComparison.OrdinalIgnoreCase))
                    return supported.Scope;

        return base.ConvertFrom(context, culture, value);
    }
}
