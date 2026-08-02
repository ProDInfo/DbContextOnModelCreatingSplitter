using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbContextOnModelCreatingSplitter;

internal static class DbContextSplitService
{
    private const string DefaultConfigurationSuffix = "Configuration";

    public static SplitResult SplitDbContextSource(
        string dbContextSource,
        Options options,
        Func<string, string?>? existingConfigurationSourceProvider = null,
        IReadOnlyDictionary<string, string>? additionalTypeRenameMap = null,
        string? configurationNamespaceOverride = null)
    {
        var root = CSharpSyntaxTree.ParseText(dbContextSource).GetRoot();
        var configurationNamespace = configurationNamespaceOverride ?? options.Namespace ?? SourceTransformService.GetFirstNamespace(root);
        var entityCalls = GetEntityInvocationExpressions(root).ToList();
        var generatedFiles = new List<GeneratedConfigurationFile>();
        var callBasedRenameMap = TypeRenameService.BuildTypeRenameMap(entityCalls.Select(GetEntityTypeFromCall), options.EntitySuffix);
        var mergedRenameMap = TypeRenameService.MergeRenameMaps(callBasedRenameMap, additionalTypeRenameMap);
        var replacementNodes = new Dictionary<SyntaxNode, SyntaxNode>();

        foreach (var entityCall in entityCalls)
        {
            var generation = BuildEntityConfiguration(
                entityCall,
                options,
                configurationNamespace,
                SourceTransformService.GetDistinctUsingDirectivesFromRoot(root),
                existingConfigurationSourceProvider);

            generatedFiles.Add(new GeneratedConfigurationFile(generation.FileName, generation.Content));

            if (!options.NoReplacement)
            {
                replacementNodes.Add(entityCall, CreateApplyConfigurationInvocation(entityCall, generation.ConfigurationClassName));
            }
        }

        var updatedRoot = replacementNodes.Count > 0
            ? root.ReplaceNodes(replacementNodes.Keys, (original, _) => replacementNodes[original])
            : root;

        if (mergedRenameMap.Count > 0)
        {
            updatedRoot = TypeRenameService.RenameTypesInSyntaxNode(updatedRoot, mergedRenameMap, renameClassDeclarations: false);
        }

        updatedRoot = PreserveDbContextClassSignature(root, updatedRoot);

        if (entityCalls.Count > 0 && !options.NoReplacement)
        {
            updatedRoot = AddConfigurationUsing(updatedRoot, configurationNamespace);
        }

        return new SplitResult(updatedRoot.ToFullString(), generatedFiles, mergedRenameMap, entityCalls.Count > 0);
    }


    private static EntityConfigurationGeneration BuildEntityConfiguration(
        InvocationExpressionSyntax entityCall,
        Options options,
        string? configurationNamespace,
        HashSet<string> baseUsings,
        Func<string, string?>? existingConfigurationSourceProvider)
    {
        var entityType = GetEntityTypeFromCall(entityCall);
        var entityTypeWithSuffix = TypeRenameService.AddSuffixToTypeName(entityType, options.EntitySuffix);
        var modelName = TypeRenameService.GetTypeName(entityTypeWithSuffix);
        var configurationSuffix = options.ConfigSuffix ?? DefaultConfigurationSuffix;
        var className = $"{modelName}{configurationSuffix}";
        var fileName = $"{className}.cs";
        var parameterName = GetEntityLambdaParameterName(entityCall);
        var configStatements = GetConfigurationStatementsFromCall(entityCall);
        var templateContext = GetConfigurationTemplateContext(
            baseUsings,
            fileName,
            existingConfigurationSourceProvider);
        var configurationCode = BuildConfigurationCode(
            className,
            entityTypeWithSuffix,
            parameterName,
            configStatements,
            templateContext.UsingStatements,
            templateContext.HeaderTrivia,
            configurationNamespace);

        return new EntityConfigurationGeneration(fileName, className, configurationCode);
    }

    private static string GetEntityTypeFromCall(InvocationExpressionSyntax entityCall)
    {
        var genericName = ((GenericNameSyntax)((MemberAccessExpressionSyntax)entityCall.Expression).Name);
        return genericName.TypeArgumentList.Arguments.First().ToString();
    }

    private static string GetEntityLambdaParameterName(InvocationExpressionSyntax entityCall)
    {
        var lambda = entityCall.ArgumentList.Arguments.FirstOrDefault()?.Expression as SimpleLambdaExpressionSyntax;
        return lambda?.Parameter.Identifier.Text ?? "e";
    }

    private static SyntaxList<StatementSyntax> GetConfigurationStatementsFromCall(InvocationExpressionSyntax entityCall)
    {
        var lambda = entityCall.ArgumentList.Arguments.FirstOrDefault()?.Expression as SimpleLambdaExpressionSyntax;
        if (lambda?.Body is BlockSyntax block)
        {
            return block.Statements;
        }

        if (lambda?.Body is ExpressionSyntax expression)
        {
            return SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ExpressionStatement(expression));
        }

        return new SyntaxList<StatementSyntax>();
    }

    private static ConfigurationTemplateContext GetConfigurationTemplateContext(
        HashSet<string> baseUsings,
        string configurationFileName,
        Func<string, string?>? existingConfigurationSourceProvider)
    {
        var usingStatements = new HashSet<string>(baseUsings);
        var headerTrivia = default(SyntaxTriviaList);

        var existingConfigurationSource = existingConfigurationSourceProvider?.Invoke(configurationFileName);
        if (!string.IsNullOrWhiteSpace(existingConfigurationSource))
        {
            var existingRoot = CSharpSyntaxTree.ParseText(existingConfigurationSource).GetRoot();
            usingStatements = SourceTransformService.GetDistinctUsingDirectivesFromRoot(existingRoot);
            headerTrivia = GetHeaderTrivia(existingRoot);
        }

        usingStatements.Add("Microsoft.EntityFrameworkCore");
        usingStatements.Add("Microsoft.EntityFrameworkCore.Metadata.Builders");

        return new ConfigurationTemplateContext(usingStatements, headerTrivia);
    }

    private static SyntaxTriviaList GetHeaderTrivia(SyntaxNode root)
    {
        return root is CompilationUnitSyntax compilationUnit
            ? compilationUnit.GetLeadingTrivia()
            : default;
    }

    private static string BuildConfigurationCode(
        string className,
        string entityTypeWithSuffix,
        string parameterName,
        SyntaxList<StatementSyntax> configStatements,
        HashSet<string> usingStatements,
        SyntaxTriviaList headerTrivia,
        string? configurationNamespace)
    {
        var markerTrivia = SyntaxFactory.TriviaList(SyntaxFactory.Comment("//__REPLACE_CONFIG__"));

        var classDeclaration = SyntaxFactory.ClassDeclaration(className)
            .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
            .AddBaseListTypes(
                SyntaxFactory.SimpleBaseType(
                    SyntaxFactory.ParseTypeName($"IEntityTypeConfiguration<{entityTypeWithSuffix}>")))
            .AddMembers(
                SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                    "Configure")
                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                .AddParameterListParameters(
                    SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameterName))
                        .WithType(SyntaxFactory.ParseTypeName($"EntityTypeBuilder<{entityTypeWithSuffix}>")))
                .WithBody(SyntaxFactory.Block(
                    SyntaxFactory.EmptyStatement().WithLeadingTrivia(markerTrivia))));

        var compilationUnit = SyntaxFactory.CompilationUnit()
            .AddUsings(usingStatements
                .Select(usingName => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(usingName)))
                .ToArray());

        compilationUnit = !string.IsNullOrWhiteSpace(configurationNamespace)
            ? compilationUnit.AddMembers(
                SyntaxFactory.FileScopedNamespaceDeclaration(
                    SyntaxFactory.ParseName(configurationNamespace))
                .AddMembers(classDeclaration))
            : compilationUnit.AddMembers(classDeclaration);

        var normalizedCompilationUnit = compilationUnit.NormalizeWhitespace();
        if (headerTrivia.Any())
        {
            normalizedCompilationUnit = normalizedCompilationUnit.WithLeadingTrivia(headerTrivia);
        }

        var finalCompilationUnit = ReplaceMarkerWithTrivia(normalizedCompilationUnit, configStatements);
        return finalCompilationUnit.ToFullString();
    }

    private static InvocationExpressionSyntax CreateApplyConfigurationInvocation(
        InvocationExpressionSyntax originalEntityCall,
        string configurationClassName)
    {
        var objectCreation = SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.IdentifierName(configurationClassName)
                    .WithLeadingTrivia(SyntaxFactory.Space))
            .WithArgumentList(SyntaxFactory.ArgumentList());

        return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("modelBuilder"),
                    SyntaxFactory.IdentifierName("ApplyConfiguration")),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(objectCreation))))
            .WithLeadingTrivia(originalEntityCall.GetLeadingTrivia())
            .WithTrailingTrivia(originalEntityCall.GetTrailingTrivia());
    }

    private static SyntaxNode AddConfigurationUsing(SyntaxNode root, string? configurationNamespace)
    {
        if (string.IsNullOrWhiteSpace(configurationNamespace))
        {
            return root;
        }

        if (root is not CompilationUnitSyntax rootCompilationUnit)
        {
            return root;
        }

        var usingDirective = SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(configurationNamespace))
            .WithUsingKeyword(
                SyntaxFactory.Token(SyntaxKind.UsingKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space));

        rootCompilationUnit = rootCompilationUnit.AddUsings(usingDirective);

        if (rootCompilationUnit.Members.Count > 0)
        {
            var firstMember = rootCompilationUnit.Members[0];
            var newFirstMember = firstMember.WithLeadingTrivia(
                SyntaxFactory.TriviaList(SyntaxFactory.CarriageReturnLineFeed)
                    .AddRange(firstMember.GetLeadingTrivia()));
            rootCompilationUnit = rootCompilationUnit.ReplaceNode(firstMember, newFirstMember);
        }

        return rootCompilationUnit;
    }

    private static IEnumerable<InvocationExpressionSyntax> GetEntityInvocationExpressions(SyntaxNode root)
    {
        var onModelCreating = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.Text == "OnModelCreating");

        if (onModelCreating is null)
        {
            return Enumerable.Empty<InvocationExpressionSyntax>();
        }

        return onModelCreating.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.Text == "Entity" &&
                memberAccess.Expression is IdentifierNameSyntax identifier &&
                identifier.Identifier.Text == "modelBuilder");
    }

    private static SyntaxNode PreserveDbContextClassSignature(SyntaxNode originalRoot, SyntaxNode updatedRoot)
    {
        var originalContextClass = GetContextClassWithOnModelCreating(originalRoot);
        var updatedContextClass = GetContextClassWithOnModelCreating(updatedRoot);
        if (originalContextClass is null || updatedContextClass is null)
        {
            return updatedRoot;
        }

        var classWithPreservedSignature = updatedContextClass
            .WithAttributeLists(originalContextClass.AttributeLists)
            .WithModifiers(originalContextClass.Modifiers)
            .WithIdentifier(originalContextClass.Identifier)
            .WithTypeParameterList(originalContextClass.TypeParameterList)
            .WithParameterList(originalContextClass.ParameterList)
            .WithBaseList(originalContextClass.BaseList)
            .WithConstraintClauses(originalContextClass.ConstraintClauses);

        return updatedRoot.ReplaceNode(updatedContextClass, classWithPreservedSignature);
    }

    private static ClassDeclarationSyntax? GetContextClassWithOnModelCreating(SyntaxNode root)
    {
        return root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(classDeclaration =>
                classDeclaration.Members
                    .OfType<MethodDeclarationSyntax>()
                    .Any(method => method.Identifier.Text == "OnModelCreating"));
    }

    private static CompilationUnitSyntax ReplaceMarkerWithTrivia(
        CompilationUnitSyntax compilationUnit,
        SyntaxList<StatementSyntax> configStatements)
    {
        var classDeclaration = compilationUnit.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (classDeclaration is null)
        {
            return compilationUnit;
        }

        var configureMethod = classDeclaration.Members.OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(method => method.Identifier.Text == "Configure" && method.Body != null);
        if (configureMethod is null)
        {
            return compilationUnit;
        }

        var marker = configureMethod.Body!.Statements
            .FirstOrDefault(statement =>
                statement.GetLeadingTrivia().Any(trivia =>
                    trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) &&
                    trivia.ToString().Contains("__REPLACE_CONFIG__")));
        if (marker is null)
        {
            return compilationUnit;
        }

        var configureWithStatements = configureMethod.ReplaceNode(marker, RemoveLeadingTabulationFromAllLines(configStatements, 4));
        var classWithConfigure = classDeclaration.ReplaceNode(configureMethod, configureWithStatements);
        return compilationUnit.ReplaceNode(classDeclaration, classWithConfigure);
    }

    private static SyntaxList<StatementSyntax> RemoveLeadingTabulationFromAllLines(SyntaxList<StatementSyntax> statements, int spaces = 8)
    {
        var spaceString = new string(' ', spaces);

        SyntaxTrivia ProcessTrivia(SyntaxTrivia trivia)
        {
            if (trivia.IsKind(SyntaxKind.WhitespaceTrivia))
            {
                var lines = trivia.ToFullString().Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.StartsWith(spaceString))
                    {
                        lines[i] = line.Substring(spaces);
                    }
                    else if (line.StartsWith("\t"))
                    {
                        lines[i] = line.Substring(1);
                    }
                }

                return SyntaxFactory.Whitespace(string.Join("\n", lines));
            }

            return trivia;
        }

        var normalizedStatements = statements.Select(statement =>
        {
            var leadingTrivia = statement.GetLeadingTrivia().Select(ProcessTrivia).ToSyntaxTriviaList();
            var statementWithAdjustedLeadingTrivia = statement.WithLeadingTrivia(leadingTrivia);

            var fullText = statementWithAdjustedLeadingTrivia.ToFullString();
            var processedText = string.Join(
                "\n",
                fullText.Split('\n').Select(line =>
                    line.StartsWith(spaceString) ? line.Substring(spaces) :
                    (line.StartsWith("\t") ? line.Substring(1) : line)));

            var parsedStatement = SyntaxFactory.ParseStatement(processedText);
            return parsedStatement.WithTriviaFrom(statementWithAdjustedLeadingTrivia);
        });

        return SyntaxFactory.List(normalizedStatements);
    }

    private sealed record ConfigurationTemplateContext(HashSet<string> UsingStatements, SyntaxTriviaList HeaderTrivia);

    private sealed record EntityConfigurationGeneration(string FileName, string ConfigurationClassName, string Content);
}
