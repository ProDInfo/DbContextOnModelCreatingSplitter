using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbContextOnModelCreatingSplitter;

internal static class SourceTransformService
{
    public static string RewriteNamespace(string source, string? newNamespace)
    {
        if (string.IsNullOrWhiteSpace(newNamespace))
        {
            return source;
        }

        var root = CSharpSyntaxTree.ParseText(source).GetRoot();
        if (root is not CompilationUnitSyntax compilationUnit)
        {
            return source;
        }

        var fileScopedNamespace = compilationUnit.Members.OfType<FileScopedNamespaceDeclarationSyntax>().FirstOrDefault();
        if (fileScopedNamespace is not null)
        {
            var updatedFileScopedNamespace = fileScopedNamespace.WithName(SyntaxFactory.ParseName(newNamespace));
            return compilationUnit.ReplaceNode(fileScopedNamespace, updatedFileScopedNamespace).ToFullString();
        }

        var blockNamespace = compilationUnit.Members.OfType<NamespaceDeclarationSyntax>().FirstOrDefault();
        if (blockNamespace is not null)
        {
            var updatedBlockNamespace = blockNamespace.WithName(SyntaxFactory.ParseName(newNamespace));
            return compilationUnit.ReplaceNode(blockNamespace, updatedBlockNamespace).ToFullString();
        }

        return source;
    }

    public static string? GetFirstNamespace(SyntaxNode root)
    {
        return root.DescendantNodes()
            .Select(node =>
                node is NamespaceDeclarationSyntax ns ? ns.Name.ToString() :
                node is FileScopedNamespaceDeclarationSyntax fs ? fs.Name.ToString() :
                null)
            .FirstOrDefault(namespaceName => namespaceName != null);
    }

    public static HashSet<string> GetDistinctUsingDirectivesFromRoot(SyntaxNode root)
    {
        return root.DescendantNodes()
            .OfType<UsingDirectiveSyntax>()
            .Select(usingDirective => usingDirective.ToString().Trim().Replace("using ", "").Replace(";", ""))
            .Distinct()
            .ToHashSet();
    }
}
