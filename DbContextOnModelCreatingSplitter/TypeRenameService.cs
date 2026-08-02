using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbContextOnModelCreatingSplitter;

internal static class TypeRenameService
{
    public static string GetTypeName(string typeName)
        => typeName.Contains('.') ? typeName[(typeName.LastIndexOf('.') + 1)..] : typeName;

    public static string AddSuffixToTypeName(string typeName, string? suffix)
    {
        if (string.IsNullOrEmpty(suffix))
        {
            return typeName;
        }

        var typeShortName = GetTypeName(typeName);
        var typePrefix = typeName.Length == typeShortName.Length
            ? string.Empty
            : typeName[..(typeName.Length - typeShortName.Length)];

        return $"{typePrefix}{typeShortName}{suffix}";
    }

    public static IReadOnlyDictionary<string, string> BuildTypeRenameMap(IEnumerable<string> typeNames, string? suffix)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return map;
        }

        foreach (var typeName in typeNames)
        {
            var shortName = GetTypeName(typeName);
            var renamed = AddSuffixToTypeName(shortName, suffix);
            if (!string.Equals(shortName, renamed, StringComparison.Ordinal))
            {
                map[shortName] = renamed;
            }
        }

        return map;
    }

    public static IReadOnlyDictionary<string, string> MergeRenameMaps(
        IReadOnlyDictionary<string, string> primaryMap,
        IReadOnlyDictionary<string, string>? additionalMap)
    {
        var merged = new Dictionary<string, string>(primaryMap, StringComparer.Ordinal);
        if (additionalMap is null)
        {
            return merged;
        }

        foreach (var kvp in additionalMap)
        {
            merged[kvp.Key] = kvp.Value;
        }

        return merged;
    }

    public static IEnumerable<string> GetDeclaredClassNames(string source)
    {
        return CSharpSyntaxTree.ParseText(source).GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Select(classDeclaration => classDeclaration.Identifier.Text);
    }

    public static SyntaxNode RenameTypesInSyntaxNode(
        SyntaxNode root,
        IReadOnlyDictionary<string, string> typeRenameMap,
        bool renameClassDeclarations)
    {
        var rewriter = new TypeRenameRewriter(typeRenameMap, renameClassDeclarations);
        return rewriter.Visit(root);
    }

    private sealed class TypeRenameRewriter : CSharpSyntaxRewriter
    {
        private readonly IReadOnlyDictionary<string, string> _typeRenameMap;
        private readonly bool _renameClassDeclarations;

        public TypeRenameRewriter(IReadOnlyDictionary<string, string> typeRenameMap, bool renameClassDeclarations)
        {
            _typeRenameMap = typeRenameMap;
            _renameClassDeclarations = renameClassDeclarations;
        }

        public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
        {
            var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
            if (!_renameClassDeclarations || !_typeRenameMap.TryGetValue(visited.Identifier.Text, out var renamed))
            {
                return visited;
            }

            var renamedIdentifier = SyntaxFactory.Identifier(
                visited.Identifier.LeadingTrivia,
                renamed,
                visited.Identifier.TrailingTrivia);

            return visited.WithIdentifier(renamedIdentifier);
        }

        public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
        {
            var visited = (IdentifierNameSyntax)base.VisitIdentifierName(node)!;
            if (!_typeRenameMap.TryGetValue(visited.Identifier.Text, out var renamed) || !IsInTypeContext(visited))
            {
                return visited;
            }

            var renamedIdentifier = SyntaxFactory.Identifier(
                visited.Identifier.LeadingTrivia,
                renamed,
                visited.Identifier.TrailingTrivia);

            return visited.WithIdentifier(renamedIdentifier);
        }

        private static bool IsInTypeContext(IdentifierNameSyntax node)
        {
            return node.Parent switch
            {
                TypeArgumentListSyntax => true,
                QualifiedNameSyntax qualifiedName => qualifiedName.Right == node || IsTypeNode(qualifiedName.Parent),
                AliasQualifiedNameSyntax => true,
                NullableTypeSyntax => true,
                ArrayTypeSyntax => true,
                PointerTypeSyntax => true,
                RefTypeSyntax => true,
                SimpleBaseTypeSyntax => true,
                VariableDeclarationSyntax variableDeclaration => variableDeclaration.Type == node,
                PropertyDeclarationSyntax propertyDeclaration => propertyDeclaration.Type == node,
                ParameterSyntax parameter => parameter.Type == node,
                MethodDeclarationSyntax methodDeclaration => methodDeclaration.ReturnType == node,
                ObjectCreationExpressionSyntax objectCreation => objectCreation.Type == node,
                ForEachStatementSyntax forEachStatement => forEachStatement.Type == node,
                CastExpressionSyntax castExpression => castExpression.Type == node,
                TypeOfExpressionSyntax typeOfExpression => typeOfExpression.Type == node,
                DefaultExpressionSyntax defaultExpression => defaultExpression.Type == node,
                TupleElementSyntax tupleElement => tupleElement.Type == node,
                DeclarationPatternSyntax declarationPattern => declarationPattern.Type == node,
                _ => IsTypeNode(node.Parent)
            };
        }

        private static bool IsTypeNode(SyntaxNode? node)
        {
            return node is TypeSyntax;
        }
    }
}
