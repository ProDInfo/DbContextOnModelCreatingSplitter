using CommandLine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DbContextOnModelCreatingSplitter
{
    public class Options
    {
        [Option('c', "dbcontext", Required = true, HelpText = "Path the DbContext file")]
        public string DbContextPath { get; set; } = null!;

        [Option('o', "outdir", Required = false, HelpText = "Output path for the generated configuration files")]
        public string? OutputDirectoryPath { get; set; }

        [Option('n', "namespace", Required = false, HelpText = "Namespace for the generated configuration classes")]
        public string? Namespace { get; set; }

        [Option('s', "suffix", Required = false, HelpText = "Suffix for the generated configuration files (Default: Configuration)")]
        public string? Suffix { get; set; }
        [Option('B', "no-backup", Required = false, HelpText = "Don't keep a copy of the original DbContext file")]
        public bool NoBackup { get; set; }

        [Option('r', "no-replace", Default = false, Required = false, HelpText = "Don't replace OnModelCreating event code")]
        public bool NoReplacement { get; set; }

        [Option('e', "embed-configs", Default = false, Required = false, HelpText = "Embed EntityTypeConfiguration class into entity model file")]
        public bool EmbedConfigsClasses { get; set; }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Parser.Default.ParseArguments<Options>(args)
                .WithParsed(Run);
        }

        private static void Run(Options options)
        {
            var dbContextFilePath = Path.GetFullPath(options.DbContextPath);
            var dbContexFolder = Path.GetDirectoryName(dbContextFilePath) ?? throw new NullReferenceException();
            var configurationsDirectoryPath = !string.IsNullOrEmpty(options.OutputDirectoryPath) ? Path.GetFullPath(options.OutputDirectoryPath) : dbContexFolder;

            var source = File.ReadAllText(dbContextFilePath);
            var tree = CSharpSyntaxTree.ParseText(source);
            SyntaxNode? root = tree.GetRoot();

            Directory.CreateDirectory(configurationsDirectoryPath);

            // Extraer todos los using (tanto globales como dentro de namespaces)
            var contextUsingStatements = GetDistinctUsingDirectivesFromRoot(root);

            // Obtiene el primer namespace (tradicional o file-scoped) encontrado en el archivo
            string? contextNamespace = GetFirstNamespace(root);

            var configurationNamespace = options.Namespace ?? contextNamespace;

            Console.WriteLine("Create configClass files:");

            string modelsBackupDirPath = string.Empty;
            if (!options.NoBackup & options.EmbedConfigsClasses)
            {
                modelsBackupDirPath = CretaeBackupDirectory(configurationsDirectoryPath);
            }

            IEnumerable<InvocationExpressionSyntax> entityCalls = GetEntityInvocationExpressions(root);

            Dictionary<SyntaxNode, SyntaxNode> replaceNodes = [];
            foreach (var call in entityCalls)
            {
                var genericName = ((GenericNameSyntax)((MemberAccessExpressionSyntax)call.Expression).Name);
                var entityType = genericName.TypeArgumentList.Arguments.First().ToString();
                var entityName = entityType.Contains('.') ? entityType.Substring(entityType.LastIndexOf('.') + 1) : entityType;
                var lambda = call.ArgumentList.Arguments.FirstOrDefault()?.Expression as SimpleLambdaExpressionSyntax;
                var parameterName = lambda?.Parameter.Identifier.Text ?? "e";
                string suffix = options.Suffix ?? "Configuration";

                // Extraer el cuerpo del lambda correctamente
                SyntaxList<StatementSyntax> configStatements;

                if (lambda?.Body is BlockSyntax block)
                {
                    // Si es un bloque, usa sus sentencias internas (sin llaves)
                    configStatements = block.Statements;
                }
                else if (lambda?.Body is ExpressionSyntax expr)
                {
                    // Si es una expresión, conviértela en una sentencia
                    configStatements = SyntaxFactory.SingletonList<StatementSyntax>(
                        SyntaxFactory.ExpressionStatement(expr));
                }
                else
                {
                    configStatements = new SyntaxList<StatementSyntax>();
                }

                var markerTrivia = SyntaxFactory.TriviaList(
                    SyntaxFactory.Comment("//__REPLACE_CONFIG__")
                );



                string configurationFilePath = Path.Combine(configurationsDirectoryPath, $"{entityName}{suffix}.cs");
                if (options.EmbedConfigsClasses && File.Exists(configurationFilePath))
                {
                    if (!options.NoBackup)
                    {
                        var backupFilePath = Path.Combine(modelsBackupDirPath, Path.GetFileName(configurationFilePath));
                        File.Copy(configurationFilePath, backupFilePath, true);
                    }

                    // Extraer todos los using (tanto globales como dentro de namespaces)
                    SyntaxNode? modelRoot = CSharpSyntaxTree.ParseText(File.ReadAllText(configurationFilePath)).GetRoot();
                    contextUsingStatements = GetDistinctUsingDirectivesFromRoot(modelRoot);
                }
                contextUsingStatements.Add("Microsoft.EntityFrameworkCore");
                contextUsingStatements.Add("Microsoft.EntityFrameworkCore.Metadata.Builders");

                // Crear el código (usings, namespace y la clase de configuración)
                var className = $"{entityName}{suffix}";
                var compilationUnit = SyntaxFactory.CompilationUnit()
                    .AddUsings(contextUsingStatements
                        .Select(u => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(u)))
                        .ToArray()
                    )
                    .AddMembers(SyntaxFactory.FileScopedNamespaceDeclaration(
                        SyntaxFactory.ParseName(configurationNamespace))
                        .AddMembers(SyntaxFactory.ClassDeclaration(className)
                        .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                        .AddBaseListTypes(
                            SyntaxFactory.SimpleBaseType(
                                SyntaxFactory.ParseTypeName($"IEntityTypeConfiguration<{entityType}>")))
                        .AddMembers(
                                SyntaxFactory.MethodDeclaration(
                                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                                    "Configure")
                                .AddModifiers(SyntaxFactory.Token(SyntaxKind.PublicKeyword))
                                .AddParameterListParameters(
                                    SyntaxFactory.Parameter(SyntaxFactory.Identifier(parameterName))
                                        .WithType(SyntaxFactory.ParseTypeName($"EntityTypeBuilder<{entityType}>")))
                                .WithBody(SyntaxFactory.Block(
                                        SyntaxFactory.EmptyStatement().WithLeadingTrivia(markerTrivia))
                                )
                            )
                        )
                    )
                    .NormalizeWhitespace();

                compilationUnit = ReplaceMarkerWithTrivia(compilationUnit, configStatements);

                // Convertir a texto
                var code = compilationUnit.ToFullString();
                File.WriteAllText(configurationFilePath, code);
                Console.WriteLine($"    {configurationFilePath}");


                if (!options.NoReplacement)
                {
                    var entityInvocation = SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("modelBuilder"),
                            SyntaxFactory.GenericName("Entity")
                                .WithTypeArgumentList(
                                    SyntaxFactory.TypeArgumentList(
                                        SyntaxFactory.SingletonSeparatedList<TypeSyntax>(
                                            SyntaxFactory.ParseTypeName(entityType)
                                        )
                                    )
                                )
                        )
                    )
                    .WithLeadingTrivia(call.GetLeadingTrivia())
                    .WithTrailingTrivia(call.GetTrailingTrivia());
                    replaceNodes.Add(call, entityInvocation);
                }
            }

            root = root.ReplaceNodes(
                replaceNodes.Keys,
                (original, _) => replaceNodes[original]
            );

            var rootCompilationUnit = root as CompilationUnitSyntax;
            bool hasUsings = rootCompilationUnit?.Members.OfType<UsingDirectiveSyntax>().Any() == true;

            string usingToAdd = configurationNamespace;
            if (rootCompilationUnit != null)
            {
                var usingName = SyntaxFactory.ParseName(usingToAdd);
                var usingDirective = SyntaxFactory.UsingDirective(usingName)
                    .WithUsingKeyword(
                        SyntaxFactory.Token(SyntaxKind.UsingKeyword)
                            .WithTrailingTrivia(SyntaxFactory.Space)
                    );
                rootCompilationUnit = rootCompilationUnit.AddUsings(usingDirective);

                // Añadir línea vacía entre usings y namespace
                if (rootCompilationUnit.Members.Count > 0)
                {
                    var firstMember = rootCompilationUnit.Members[0];
                    var newFirstMember = firstMember.WithLeadingTrivia(
                        SyntaxFactory.TriviaList(
                            SyntaxFactory.CarriageReturnLineFeed
                        ).AddRange(firstMember.GetLeadingTrivia())
                    );

                    rootCompilationUnit = rootCompilationUnit.ReplaceNode(firstMember, newFirstMember);
                }
                root = rootCompilationUnit;
            }



            if (!entityCalls.Any())
            {
                Console.WriteLine("    No entity definitions found.");
                return;
            }

            if (!options.NoBackup)
            {
                if (options.EmbedConfigsClasses)
                    Console.WriteLine($"Backup models files saved at: {modelsBackupDirPath}");
                Console.WriteLine();

                var backupFilePath = Path.Combine(dbContexFolder, $"{Path.GetFileName(dbContextFilePath)}.{DateTime.Now:yyyyMMddHHmmss}.bak");
                Console.WriteLine("Backup DbContext file:");
                Console.WriteLine($"    Original file path: {dbContextFilePath}");
                Console.WriteLine($"    Original file path: {backupFilePath}");
                File.Copy(dbContextFilePath, backupFilePath, true);
            }

            source = root.ToFullString();

            File.WriteAllText(dbContextFilePath, source);
        }

        private static IEnumerable<InvocationExpressionSyntax> GetEntityInvocationExpressions(SyntaxNode root)
        {
            var onModelCreating = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .First(m => m.Identifier.Text == "OnModelCreating");

            var entityCalls = onModelCreating.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(inv =>
                    inv.Expression is MemberAccessExpressionSyntax memberAccess &&
                    memberAccess.Name.Identifier.Text == "Entity" &&
                    memberAccess.Expression is IdentifierNameSyntax idName &&
                    idName.Identifier.Text == "modelBuilder"
                );
            return entityCalls;
        }

        private static string CretaeBackupDirectory(string configurationsDirectoryPath)
        {
            string modelsBackupDirPath;
            var modelsBackupDir = $"_backup_{DateTime.Now:yyyyMMddHHmmss}";
            modelsBackupDirPath = Path.Combine(configurationsDirectoryPath, modelsBackupDir);
            Directory.CreateDirectory(modelsBackupDirPath);
            return modelsBackupDirPath;
        }

        private static string? GetFirstNamespace(SyntaxNode root)
        {
            return root.DescendantNodes()
                .Select(n =>
                    n is NamespaceDeclarationSyntax ns ? ns.Name.ToString() :
                    n is FileScopedNamespaceDeclarationSyntax fs ? fs.Name.ToString() :
                    null)
                .FirstOrDefault(n => n != null);
        }

        private static CompilationUnitSyntax ReplaceMarkerWithTrivia(CompilationUnitSyntax compilationUnit, SyntaxList<StatementSyntax> configStatements)
        {
            // Buscar la primera clase en el árbol
            var classDecl = compilationUnit.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDecl is null) { return compilationUnit; }

            var configure = classDecl.Members.OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "Configure" && m.Body != null);
            if (configure is null) { return compilationUnit; }

            // Buscar el statement con el comentario marcador
            var marker = configure.Body!.Statements
                .FirstOrDefault(st =>
                    st.GetLeadingTrivia().Any(tr => tr.IsKind(SyntaxKind.SingleLineCommentTrivia) && tr.ToString().Contains("__REPLACE_CONFIG__")));
            if (marker is null) { return compilationUnit; }

            // Crear el nuevo método Configure con las sentencias reales
            var newConfigure = configure.ReplaceNode(marker, RemoveLeadingTabulationFromAllLines(configStatements, 4));
            var newClassDecl = classDecl.ReplaceNode(configure, newConfigure);
            return compilationUnit.ReplaceNode(classDecl, newClassDecl);
        }

        private static SyntaxList<StatementSyntax> RemoveLeadingTabulationFromAllLines(SyntaxList<StatementSyntax> statements, int spaces = 8)
        {
            var spaceString = new string(' ', spaces);

            SyntaxTrivia ProcessTrivia(SyntaxTrivia trivia)
            {
                if (trivia.IsKind(SyntaxKind.WhitespaceTrivia))
                {
                    var lines = trivia.ToFullString().Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i];
                        if (line.StartsWith(spaceString))
                            lines[i] = line.Substring(spaces);
                        else if (line.StartsWith("\t"))
                            lines[i] = line.Substring(1);
                    }
                    return SyntaxFactory.Whitespace(string.Join("\n", lines));
                }
                return trivia;
            }

            var newStatements = statements.Select(st =>
            {
                var leading = st.GetLeadingTrivia().Select(ProcessTrivia).ToSyntaxTriviaList();
                var node = st.WithLeadingTrivia(leading);

                // También procesar el interior del statement si es una expresión multilínea
                var fullText = node.ToFullString();
                var processedText = string.Join(
                    "\n",
                    fullText.Split('\n').Select(line =>
                        line.StartsWith(spaceString) ? line.Substring(spaces) :
                        (line.StartsWith("\t") ? line.Substring(1) : line)
                    )
                );
                // Parsear de nuevo el statement para asegurar el ajuste en todas las líneas
                var parsed = SyntaxFactory.ParseStatement(processedText);
                return parsed.WithTriviaFrom(node);
            });

            return SyntaxFactory.List(newStatements);
        }

        private static HashSet<string> GetDistinctUsingDirectivesFromRoot(SyntaxNode root)
        {
            return root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Select(u => u.ToFullString().Trim().Replace("using ", "").Replace(";", ""))
                .Distinct()
                .ToHashSet();
        }
    }
}