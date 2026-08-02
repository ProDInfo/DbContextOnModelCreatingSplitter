using CommandLine;

namespace DbContextOnModelCreatingSplitter;

public class Options
{
    [Option('c', "context-file", Required = true, HelpText = "DbContext file name or path")]
    public string DbContextPath { get; set; } = null!;

    [Option("input-dir", Required = false, HelpText = "Input directory for entity/model files (Default: context file directory)")]
    public string? InputDirectoryPath { get; set; }

    [Option('o', "output-dir", Required = false, HelpText = "Base output directory")]
    public string? OutputDirectoryPath { get; set; }

    [Option("context-subdir", Required = false, HelpText = "DbContext output subdirectory over output-dir (Default: .)")]
    public string? ContextSubdirectoryPath { get; set; }

    [Option("entities-subdir", Required = false, HelpText = "Entities output subdirectory over output-dir (Default: Entities)")]
    public string? EntitiesSubdirectoryPath { get; set; }

    [Option("configs-subdir", Required = false, HelpText = "Configurations output subdirectory over output-dir (Default: Configurations)")]
    public string? ConfigurationsSubdirectoryPath { get; set; }

    [Option('n', "namespace", Required = false, HelpText = "Namespace for the generated configuration classes")]
    public string? Namespace { get; set; }

    [Option('s', "config-suffix", Required = false, HelpText = "Suffix for the generated configuration files (Default: Configuration)")]
    public string? ConfigSuffix { get; set; }

    [Option('t', "entity-suffix", Required = false, HelpText = "Suffix for the generated entity/model class names")]
    public string? EntitySuffix { get; set; }

    [Option('B', "no-backup", Required = false, HelpText = "Don't keep a copy of the original DbContext file")]
    public bool NoBackup { get; set; }

    [Option('r', "no-replace", Default = false, Required = false, HelpText = "Don't replace OnModelCreating event code")]
    public bool NoReplacement { get; set; }

    [Option('e', "embed-configs", Default = false, Required = false, HelpText = "Embed EntityTypeConfiguration class into entity model file")]
    public bool EmbedConfigsClasses { get; set; }
}

public sealed record GeneratedConfigurationFile(string FileName, string Content);

public sealed record SplitResult(
    string UpdatedDbContextSource,
    IReadOnlyList<GeneratedConfigurationFile> GeneratedFiles,
    IReadOnlyDictionary<string, string> TypeRenameMap,
    bool HasEntityDefinitions);

public class Program
{
    public static void Main(string[] args)
    {
        Parser.Default.ParseArguments<Options>(args)
            .WithParsed(Run);
    }

    internal static void Run(Options options)
    {
        var pathLayout = PathLayoutService.ResolvePathLayout(options);

        Directory.CreateDirectory(pathLayout.ContextOutputDirectoryPath);
        Directory.CreateDirectory(pathLayout.EntitiesOutputDirectoryPath);
        Directory.CreateDirectory(pathLayout.ConfigurationsOutputDirectoryPath);

        var dbContextInputSource = File.ReadAllText(pathLayout.DbContextInputFilePath);
        var entitySourcesByOutputPath = EntitySourceFileService.ReadEntitySourcesByOutputPath(
            pathLayout.EntityInputFilePaths,
            pathLayout.EntitiesInputDirectoryPath,
            pathLayout.EntitiesOutputDirectoryPath);
        var entityTypeRenameMap = EntitySourceFileService.BuildEntityTypeRenameMap(entitySourcesByOutputPath.Values, options.EntitySuffix);

        Console.WriteLine("Create Configuration class files:");

        string? ReadExistingConfigurationSource(string configurationFileName)
        {
            if (!options.EmbedConfigsClasses)
            {
                return null;
            }

            var configurationFilePath = Path.Combine(pathLayout.ConfigurationsOutputDirectoryPath, configurationFileName);
            if (!File.Exists(configurationFilePath))
            {
                return null;
            }

            if (!string.IsNullOrEmpty(pathLayout.InputBackupPath))
            {
                var backupFilePath = Path.Combine(pathLayout.InputBackupPath, Path.GetFileName(configurationFilePath));
                File.Copy(configurationFilePath, backupFilePath, true);
            }

            return File.ReadAllText(configurationFilePath);
        }

        var splitResult = DbContextSplitService.SplitDbContextSource(
            dbContextInputSource,
            options,
            ReadExistingConfigurationSource,
            entityTypeRenameMap,
            pathLayout.ConfigurationNamespace);

        if (splitResult.HasEntityDefinitions)
        {
            foreach (var generatedFile in splitResult.GeneratedFiles)
            {
                var configurationFilePath = Path.Combine(pathLayout.ConfigurationsOutputDirectoryPath, generatedFile.FileName);
                File.WriteAllText(configurationFilePath, generatedFile.Content);
                Console.WriteLine($"    {configurationFilePath}");
            }
        }
        else
        {
            Console.WriteLine("    No entity definitions found.");
        }

        var dbContextOutputSource = SourceTransformService.RewriteNamespace(splitResult.UpdatedDbContextSource, pathLayout.ContextNamespace);
        var dbContextChanged = !string.Equals(dbContextInputSource, dbContextOutputSource, StringComparison.Ordinal)
                               || !string.Equals(pathLayout.DbContextInputFilePath, pathLayout.DbContextOutputFilePath, StringComparison.OrdinalIgnoreCase);

        Console.WriteLine("Creating Entity class files:");
        var renamedEntityOutputSources = EntitySourceFileService.RenameEntitySourcesByPath(entitySourcesByOutputPath, splitResult.TypeRenameMap, options.EntitySuffix);
        var rewrittenEntityOutputSources = renamedEntityOutputSources.ToDictionary(
            kvp => kvp.Key,
            kvp => SourceTransformService.RewriteNamespace(kvp.Value, pathLayout.EntitiesNamespace),
            StringComparer.OrdinalIgnoreCase);

        EntitySourceFileService.DeleteObsoleteEntityFiles(entitySourcesByOutputPath.Keys, rewrittenEntityOutputSources.Keys);
        EntitySourceFileService.WriteEntityOutputs(rewrittenEntityOutputSources, pathLayout.InputBackupPath);
        foreach (var entityFilePath in rewrittenEntityOutputSources.Keys)
        {
            Console.WriteLine($"    {entityFilePath}");
        }

        if (!string.IsNullOrEmpty(pathLayout.InputBackupPath))
        {
            Console.WriteLine($"Backup files saved at: {pathLayout.InputBackupPath}");

            Console.WriteLine();
            var backupFilePath = Path.Combine(pathLayout.InputBackupPath, $"{Path.GetFileName(pathLayout.DbContextInputFilePath)}");
            Console.WriteLine("Backup DbContext file:");
            Console.WriteLine($"    Original file path: {pathLayout.DbContextInputFilePath}");
            Console.WriteLine($"    Original file path: {backupFilePath}");
            File.Copy(pathLayout.DbContextInputFilePath, backupFilePath, true);
        }

        File.WriteAllText(pathLayout.DbContextOutputFilePath, dbContextOutputSource);
    }
}
