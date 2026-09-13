using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace RoslynQuery.ReferenceGraph;

/// <summary>
/// Buckets a single reference occurrence into a <see cref="ReferenceUsageKind"/>. Shared by both
/// graph directions so the UI's one filter means the same thing above and below a node.
/// </summary>
internal static class ReferenceUsageClassifier
{
    public static ReferenceUsageKind Classify(SyntaxNode occurrence, ISymbol target)
    {
        if (occurrence is null) return ReferenceUsageKind.Read;

        if (occurrence.Ancestors().OfType<CrefSyntax>().Any()) return ReferenceUsageKind.Documentation;

        // `this()`/`base()` bind at the keyword, so there is no name node to climb from.
        if (occurrence is ConstructorInitializerSyntax || occurrence.Parent is ConstructorInitializerSyntax)
            return ReferenceUsageKind.Construction;

        var expr = ClimbName(occurrence);
        var parent = expr.Parent;

        if (IsConstruction(expr, parent, target)) return ReferenceUsageKind.Construction;

        // A name in type position can never be a value read or write, whatever else surrounds it.
        if (target is ITypeSymbol || target is INamespaceSymbol || IsSyntacticTypeReference(expr))
            return ReferenceUsageKind.TypeReference;

        if (parent is InvocationExpressionSyntax invocation && invocation.Expression == expr)
            return ReferenceUsageKind.Invocation;

        switch (parent)
        {
            case AssignmentExpressionSyntax assignment when assignment.Left == expr:
                if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression)) return ReferenceUsageKind.Write;
                // += / -= on an event is a subscription, not a read-modify-write of a value.
                if (target is IEventSymbol) return ReferenceUsageKind.Write;
                return ReferenceUsageKind.Read | ReferenceUsageKind.Write;

            case ArgumentSyntax argument:
                switch (argument.RefKindKeyword.Kind())
                {
                    case SyntaxKind.OutKeyword: return ReferenceUsageKind.Write;
                    case SyntaxKind.RefKeyword: return ReferenceUsageKind.Read | ReferenceUsageKind.Write;
                    default: return ReferenceUsageKind.Read;
                }

            case PrefixUnaryExpressionSyntax prefix when IsIncrementOrDecrement(prefix.Kind()):
                return ReferenceUsageKind.Write;

            case PostfixUnaryExpressionSyntax postfix when IsIncrementOrDecrement(postfix.Kind()):
                return ReferenceUsageKind.Write;

            default:
                return ReferenceUsageKind.Read;
        }
    }

    /// <summary>
    /// The occurrence is the name of an attribute being applied, rather than something mentioned inside
    /// its arguments. Needed because an attribute application binds to the attribute's constructor, not
    /// to its type, so <c>Applied To</c> cannot be recognised from the target symbol alone.
    /// </summary>
    public static bool IsAttributeApplication(SyntaxNode occurrence)
    {
        if (occurrence is null) return false;

        var expr = ClimbName(occurrence);

        for (var node = expr; node != null; node = node.Parent)
            if (node is AttributeSyntax attribute) return attribute.Name.Span.Contains(expr.SpanStart);

        return false;
    }

    /// <summary>
    /// The occurrence sits in a member's signature - a parameter, a return or member type, a base list, a
    /// type-parameter constraint - rather than inside a body. This is what separates "exposed by" from an
    /// ordinary type mention such as a cast, a <c>typeof</c>, or a local's declared type.
    /// </summary>
    public static bool IsSignaturePosition(SyntaxNode occurrence)
    {
        if (occurrence is null) return false;

        var expr = ClimbName(occurrence);

        for (var node = expr; node != null; node = node.Parent)
        {
            switch (node)
            {
                // A lambda's parameter list is inside a body, so it exposes nothing.
                case ParameterSyntax parameter:
                    return !(parameter.Parent?.Parent is AnonymousFunctionExpressionSyntax);

                case BaseListSyntax _:
                case TypeParameterConstraintClauseSyntax _:
                    return true;

                case MethodDeclarationSyntax method: return Covers(method.ReturnType, expr);
                case PropertyDeclarationSyntax property: return Covers(property.Type, expr);
                case IndexerDeclarationSyntax indexer: return Covers(indexer.Type, expr);
                case EventDeclarationSyntax @event: return Covers(@event.Type, expr);
                case DelegateDeclarationSyntax @delegate: return Covers(@delegate.ReturnType, expr);
                case OperatorDeclarationSyntax @operator: return Covers(@operator.ReturnType, expr);
                case ConversionOperatorDeclarationSyntax conversion: return Covers(conversion.Type, expr);
                case FieldDeclarationSyntax field: return Covers(field.Declaration?.Type, expr);
                case EventFieldDeclarationSyntax eventField: return Covers(eventField.Declaration?.Type, expr);

                case BlockSyntax _:
                case ArrowExpressionClauseSyntax _:
                    return false;
            }
        }

        return false;
    }

    private static bool Covers(SyntaxNode part, SyntaxNode occurrence) =>
        part != null && part.Span.Contains(occurrence.Span);

    /// <summary>
    /// Walks up the qualifiers that still denote the same symbol, so `a.B.C` classifies by what
    /// surrounds the whole expression rather than by the member-access node itself.
    /// </summary>
    private static SyntaxNode ClimbName(SyntaxNode node)
    {
        while (true)
        {
            switch (node.Parent)
            {
                case MemberAccessExpressionSyntax member when member.Name == node:
                case MemberBindingExpressionSyntax binding when binding.Name == node:
                case QualifiedNameSyntax qualified when qualified.Right == node:
                case AliasQualifiedNameSyntax alias when alias.Name == node:
                    node = node.Parent;
                    continue;
                default:
                    return node;
            }
        }
    }

    private static bool IsConstruction(SyntaxNode expr, SyntaxNode parent, ISymbol target)
    {
        if (target is IMethodSymbol method && method.MethodKind == MethodKind.Constructor) return true;
        if (parent is ObjectCreationExpressionSyntax creation && creation.Type == expr) return true;
        if (expr is ImplicitObjectCreationExpressionSyntax) return true;
        return false;
    }

    private static bool IsIncrementOrDecrement(SyntaxKind kind) =>
        kind == SyntaxKind.PreIncrementExpression
        || kind == SyntaxKind.PreDecrementExpression
        || kind == SyntaxKind.PostIncrementExpression
        || kind == SyntaxKind.PostDecrementExpression;

    /// <summary>Fallback for occurrences whose symbol did not bind (or bound to an alias).</summary>
    private static bool IsSyntacticTypeReference(SyntaxNode expr)
    {
        if (expr is not TypeSyntax) return false;

        switch (expr.Parent)
        {
            case BaseTypeSyntax _:
            case TypeArgumentListSyntax _:
            case ArrayTypeSyntax _:
            case NullableTypeSyntax _:
            case PointerTypeSyntax _:
            case TypeConstraintSyntax _:
            case CatchDeclarationSyntax _:
            case AttributeSyntax _:
            case UsingDirectiveSyntax _:
            case ExplicitInterfaceSpecifierSyntax _:
            case QualifiedNameSyntax _:
            case AliasQualifiedNameSyntax _:
            case ArrayCreationExpressionSyntax _:
            case StackAllocArrayCreationExpressionSyntax _:
            case TypePatternSyntax _:
                return true;
            case ParameterSyntax parameter: return parameter.Type == expr;
            case VariableDeclarationSyntax variable: return variable.Type == expr;
            case CastExpressionSyntax cast: return cast.Type == expr;
            case TypeOfExpressionSyntax typeOf: return typeOf.Type == expr;
            case SizeOfExpressionSyntax sizeOf: return sizeOf.Type == expr;
            case DefaultExpressionSyntax defaultOf: return defaultOf.Type == expr;
            case DeclarationExpressionSyntax declaration: return declaration.Type == expr;
            case DeclarationPatternSyntax pattern: return pattern.Type == expr;
            case MethodDeclarationSyntax method: return method.ReturnType == expr;
            case PropertyDeclarationSyntax property: return property.Type == expr;
            case EventDeclarationSyntax @event: return @event.Type == expr;
            case IndexerDeclarationSyntax indexer: return indexer.Type == expr;
            case DelegateDeclarationSyntax @delegate: return @delegate.ReturnType == expr;
            case OperatorDeclarationSyntax @operator: return @operator.ReturnType == expr;
            case ConversionOperatorDeclarationSyntax conversion: return conversion.Type == expr;
            case BinaryExpressionSyntax binary:
                return binary.Right == expr && (binary.IsKind(SyntaxKind.AsExpression) || binary.IsKind(SyntaxKind.IsExpression));
            default:
                return false;
        }
    }
}
