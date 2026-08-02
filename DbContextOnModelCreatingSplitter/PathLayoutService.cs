using Microsoft.CodeAnalysis.CSharp;

namespace DbContextOnModelCreatingSplitter;

internal static class PathLayoutService
{
    public static bool ShouldRenameEntityInputFiles(
        string entitiesInputDirectoryPath,
        string entitiesOutputDirectoryPath)
    {
        var normalizedEntitiesInputDirectoryPath = Path.GetFullPath(entitiesInputDirectoryPath);
        var normalizedEntitiesOutputDirectoryPath = Path.GetFullPath(entitiesOutputDirectoryPath);
        return string.Equals(
            normalizedEntitiesInputDirectoryPath,
            normalizedEntitiesOutputDirectoryPath,
            StringComparison.OrdinalIgnoreCase);
    }

    public static PathLayout ResolvePathLayout(Options options)
    {
        var dbContextInputFilePath = Path.GetFullPath(options.DbContextPath);
        if (!File.Exists(dbContextInputFilePath))
        {
            throw new FileNotFoundException($"DbContext file not found: {dbContextInputFilePath}", dbContextInputFilePath);
        }

        var contextInputDirectory = Path.GetDirectoryName(dbContextInputFilePath) ?? Path.GetFullPath(".");

        var outputDirectoryPath = Path.GetFullPath(options.OutputDirectoryPath ?? contextInputDirectory);
        var contextSubdirectory = NormalizeSubdirectory(options.ContextSubdirectoryPath ?? ".");
        var entitiesSubdirectory = NormalizeSubdirectory(options.EntitiesSubdirectoryPath ?? "Entities");
        var configurationsSubdirectory = NormalizeSubdirectory(options.ConfigurationsSubdirectoryPath ?? "Configurations");

        var contextOutputDirectoryPath = ResolveOutputSubdirectoryPath(outputDirectoryPath, contextSubdirectory, "--context-subdir");
        var entitiesOutputDirectoryPath = ResolveOutputSubdirectoryPath(outputDirectoryPath, entitiesSubdirectory, "--entities-subdir");
        var configurationsOutputDirectoryPath = ResolveOutputSubdirectoryPath(outputDirectoryPath, configurationsSubdirectory, "--configs-subdir");

        var entitiesInputDirectoryPath = Path.GetFullPath(options.InputDirectoryPath ?? contextInputDirectory);
        var candidateEntityInputFilePaths = Directory.Exists(entitiesInputDirectoryPath)
            ? Directory.GetFiles(entitiesInputDirectoryPath, "*.cs", SearchOption.AllDirectories).ToList()
            : [];
        var entityInputFilePaths = FilterEntityFilePaths(candidateEntityInputFilePaths, dbContextInputFilePath);

        var dbContextInputFileBackupPath = options.NoBackup ? null : dbContextInputFilePath + ".bak";

        string? inputBackupPath = null;
        if (contextInputDirectory == contextOutputDirectoryPath && !options.NoBackup)
        {
            inputBackupPath = CreateBackupDirectory(contextInputDirectory);
        }
        var dbContextSource = File.ReadAllText(dbContextInputFilePath);
        var baseNamespace = options.Namespace ?? SourceTransformService.GetFirstNamespace(CSharpSyntaxTree.ParseText(dbContextSource).GetRoot());

        return new PathLayout(
            dbContextInputFilePath,
            entitiesInputDirectoryPath,
            entityInputFilePaths,
            inputBackupPath,
            contextOutputDirectoryPath,
            entitiesOutputDirectoryPath,
            configurationsOutputDirectoryPath,
            BuildNamespaceForSubdirectory(baseNamespace, contextSubdirectory),
            BuildNamespaceForSubdirectory(baseNamespace, entitiesSubdirectory),
            BuildNamespaceForSubdirectory(baseNamespace, configurationsSubdirectory));
    }

    public static string CreateBackupDirectory(string contextDirectoryPath)
    {
        var modelsBackupDir = $"_backup_{DateTime.Now:yyyyMMddHHmmss}";
        var modelsBackupDirPath = Path.Combine(contextDirectoryPath, modelsBackupDir);
        Directory.CreateDirectory(modelsBackupDirPath);
        return modelsBackupDirPath;
    }

    public static string? BuildNamespaceForSubdirectory(string? baseNamespace, string subdirectory)
    {
        if (string.IsNullOrWhiteSpace(baseNamespace))
        {
            return null;
        }

        var normalizedSubdirectory = NormalizeSubdirectory(subdirectory);
        if (normalizedSubdirectory == ".")
        {
            return baseNamespace;
        }

        var namespaceSegments = normalizedSubdirectory
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .Select(segment => segment.Trim())
            .Where(segment => segment.Length > 0);

        return string.Join('.', new[] { baseNamespace }.Concat(namespaceSegments));
    }

    public static IReadOnlyList<string> FilterEntityFilePaths(IEnumerable<string> candidateEntityFiles, string dbContextFilePath)
    {
        var contextFileFullPath = Path.GetFullPath(dbContextFilePath);
        return candidateEntityFiles
            .Select(Path.GetFullPath)
            .Where(path => !string.Equals(path, contextFileFullPath, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static string ResolveOutputSubdirectoryPath(string outputDirectoryPath, string subdirectory, string optionName)
    {
        var normalizedSubdirectory = NormalizeSubdirectory(subdirectory);
        if (Path.IsPathRooted(normalizedSubdirectory))
        {
            throw new InvalidOperationException($"{optionName} must be a relative subdirectory.");
        }

        var outputDirectoryFullPath = Path.GetFullPath(outputDirectoryPath);
        var candidatePath = Path.GetFullPath(Path.Combine(outputDirectoryFullPath, normalizedSubdirectory));
        var relativePath = Path.GetRelativePath(outputDirectoryFullPath, candidatePath);
        var escapesOutputDirectory =
            relativePath.StartsWith("..", StringComparison.Ordinal) ||
            relativePath.StartsWith($@"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($@"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath);

        if (escapesOutputDirectory)
        {
            throw new InvalidOperationException($"{optionName} must stay inside output-dir.");
        }

        return candidatePath;
    }

    private static string NormalizeSubdirectory(string subdirectory)
    {
        if (string.IsNullOrWhiteSpace(subdirectory))
        {
            return ".";
        }

        var trimmed = subdirectory.Trim();
        return trimmed == "." ? "." : trimmed.TrimStart('\\', '/');
    }
}
