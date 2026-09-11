using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;

namespace RoslynQuery.ToolWindow;

/// <summary>The editor classification that colours each run of a signature.</summary>
internal static class SignatureClassification
{
    public static string NameFor(SymbolDisplayPartKind kind)
    {
        switch (kind)
        {
            case SymbolDisplayPartKind.ClassName: return ClassificationTypeNames.ClassName;
            case SymbolDisplayPartKind.RecordClassName: return ClassificationTypeNames.RecordClassName;
            case SymbolDisplayPartKind.StructName: return ClassificationTypeNames.StructName;
            case SymbolDisplayPartKind.RecordStructName: return ClassificationTypeNames.RecordStructName;
            case SymbolDisplayPartKind.InterfaceName: return ClassificationTypeNames.InterfaceName;
            case SymbolDisplayPartKind.EnumName: return ClassificationTypeNames.EnumName;
            case SymbolDisplayPartKind.DelegateName: return ClassificationTypeNames.DelegateName;
            case SymbolDisplayPartKind.TypeParameterName: return ClassificationTypeNames.TypeParameterName;
            case SymbolDisplayPartKind.NamespaceName: return ClassificationTypeNames.NamespaceName;
            case SymbolDisplayPartKind.ModuleName: return ClassificationTypeNames.ModuleName;
            case SymbolDisplayPartKind.MethodName: return ClassificationTypeNames.MethodName;
            case SymbolDisplayPartKind.ExtensionMethodName: return ClassificationTypeNames.ExtensionMethodName;
            case SymbolDisplayPartKind.PropertyName: return ClassificationTypeNames.PropertyName;
            case SymbolDisplayPartKind.FieldName: return ClassificationTypeNames.FieldName;
            case SymbolDisplayPartKind.ConstantName: return ClassificationTypeNames.ConstantName;
            case SymbolDisplayPartKind.EnumMemberName: return ClassificationTypeNames.EnumMemberName;
            case SymbolDisplayPartKind.EventName: return ClassificationTypeNames.EventName;
            case SymbolDisplayPartKind.LocalName:
            case SymbolDisplayPartKind.RangeVariableName:
                return ClassificationTypeNames.LocalName;
            case SymbolDisplayPartKind.ParameterName: return ClassificationTypeNames.ParameterName;
            case SymbolDisplayPartKind.LabelName: return ClassificationTypeNames.LabelName;
            case SymbolDisplayPartKind.Keyword: return ClassificationTypeNames.Keyword;
            case SymbolDisplayPartKind.Operator: return ClassificationTypeNames.Operator;
            case SymbolDisplayPartKind.Punctuation: return ClassificationTypeNames.Punctuation;
            case SymbolDisplayPartKind.NumericLiteral: return ClassificationTypeNames.NumericLiteral;
            case SymbolDisplayPartKind.StringLiteral: return ClassificationTypeNames.StringLiteral;
            case SymbolDisplayPartKind.AliasName:
            case SymbolDisplayPartKind.AssemblyName:
            case SymbolDisplayPartKind.ErrorTypeName:
            case SymbolDisplayPartKind.AnonymousTypeIndicator:
                return ClassificationTypeNames.Identifier;
            default:
                return ClassificationTypeNames.Text;
        }
    }

    public static bool IsEmphasised(SymbolDisplayPartKind kind)
    {
        switch (kind)
        {
            case SymbolDisplayPartKind.MethodName:
            case SymbolDisplayPartKind.ExtensionMethodName:
            case SymbolDisplayPartKind.PropertyName:
            case SymbolDisplayPartKind.FieldName:
            case SymbolDisplayPartKind.ConstantName:
            case SymbolDisplayPartKind.EnumMemberName:
            case SymbolDisplayPartKind.EventName:
                return true;
            default:
                return false;
        }
    }
}
