using Microsoft.CodeAnalysis.CSharp;

namespace DbContextOnModelCreatingSplitter;

internal static class EntitySourceFileService
{
    public static IReadOnlyDictionary<string, string> ReadEntitySourcesByOutputPath(
        IEnumerable<string> entityInputFilePaths,
        string entitiesInputDirectoryPath,
        string entitiesOutputDirectoryPath)
    {
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var inputFilePath in entityInputFilePaths)
        {
            var relativePath = Path.GetRelativePath(entitiesInputDirectoryPath, inputFilePath);
            var outputPath = Path.Combine(entitiesOutputDirectoryPath, relativePath);
            sources[outputPath] = File.ReadAllText(inputFilePath);
        }

        return sources;
    }

    public static bool WriteEntityOutputs(IReadOnlyDictionary<string, string> entitySourcesByPath, string? backupPath)
    {
        var changed = false;
        foreach (var entitySource in entitySourcesByPath)
        {
            var exists = File.Exists(entitySource.Key);
            var current = exists ? File.ReadAllText(entitySource.Key) : null;
            //Make a backup of the current file if it exists and a backup path is provided
            if (!string.IsNullOrEmpty(backupPath) && current != null)
            {
                var backupFilePath = Path.Combine(backupPath, Path.GetFileName(entitySource.Key));
                File.WriteAllText(backupFilePath, current);
            }
            //// Write the new content if the file doesn't exist or if the content has changed
            //if (!exists || !string.Equals(current, entitySource.Value, StringComparison.Ordinal))
            //{
            File.WriteAllText(entitySource.Key, entitySource.Value);
            //    changed = true;
            //}
        }
        return changed;
    }

    public static bool DeleteObsoleteEntityFiles(
        IEnumerable<string> originalEntityPaths,
        IEnumerable<string> updatedEntityPaths)
    {
        var changed = false;
        var updatedPathSet = new HashSet<string>(
            updatedEntityPaths.Select(Path.GetFullPath),
            StringComparer.OrdinalIgnoreCase);

        foreach (var originalPath in originalEntityPaths.Select(Path.GetFullPath))
        {
            if (updatedPathSet.Contains(originalPath) || !File.Exists(originalPath))
            {
                continue;
            }

            File.Delete(originalPath);
            changed = true;
        }

        return changed;
    }

    public static IReadOnlyDictionary<string, string> BuildEntityTypeRenameMap(IEnumerable<string> entitySources, string? entitySuffix)
    {
        if (string.IsNullOrWhiteSpace(entitySuffix))
        {
            return new Dictionary<string, string>();
        }

        var classNames = entitySources
            .SelectMany(TypeRenameService.GetDeclaredClassNames)
            .Distinct()
            .ToList();

        return TypeRenameService.BuildTypeRenameMap(classNames, entitySuffix);
    }

    public static string RenameEntitySource(string entitySource, IReadOnlyDictionary<string, string> typeRenameMap)
    {
        if (typeRenameMap.Count == 0)
        {
            return entitySource;
        }

        var root = CSharpSyntaxTree.ParseText(entitySource).GetRoot();
        var renamedRoot = TypeRenameService.RenameTypesInSyntaxNode(root, typeRenameMap, renameClassDeclarations: true);
        return renamedRoot.ToFullString();
    }

    public static string GetRenamedEntityFilePath(
        string filePath,
        IReadOnlyDictionary<string, string> typeRenameMap,
        string? entitySuffix)
    {
        if (string.IsNullOrWhiteSpace(entitySuffix))
        {
            return filePath;
        }

        var extension = Path.GetExtension(filePath);
        if (!string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase))
        {
            return filePath;
        }

        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);
        if (fileNameWithoutExtension.EndsWith(entitySuffix, StringComparison.Ordinal))
        {
            return filePath;
        }

        if (!typeRenameMap.TryGetValue(fileNameWithoutExtension, out var renamedFileName))
        {
            return filePath;
        }

        var directory = Path.GetDirectoryName(filePath) ?? string.Empty;
        return Path.Combine(directory, $"{renamedFileName}{extension}");
    }

    public static IReadOnlyDictionary<string, string> RenameEntitySourcesByPath(
        IReadOnlyDictionary<string, string> entitySourcesByPath,
        IReadOnlyDictionary<string, string> typeRenameMap,
        string? entitySuffix)
    {
        var renamedSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pathChangedEntries = new List<(string Path, string Content)>();
        foreach (var entitySourceByPath in entitySourcesByPath)
        {
            var renamedContent = RenameEntitySource(entitySourceByPath.Value, typeRenameMap);
            var renamedPath = GetRenamedEntityFilePath(entitySourceByPath.Key, typeRenameMap, entitySuffix);
            var pathChanged = !string.Equals(entitySourceByPath.Key, renamedPath, StringComparison.OrdinalIgnoreCase);
            if (pathChanged)
            {
                pathChangedEntries.Add((renamedPath, renamedContent));
            }
            else
            {
                renamedSources[renamedPath] = renamedContent;
            }
        }

        foreach (var pathChangedEntry in pathChangedEntries)
        {
            renamedSources[pathChangedEntry.Path] = pathChangedEntry.Content;
        }

        return renamedSources;
    }
}
