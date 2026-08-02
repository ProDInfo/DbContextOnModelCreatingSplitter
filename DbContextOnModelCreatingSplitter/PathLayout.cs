namespace DbContextOnModelCreatingSplitter;

internal sealed record PathLayout(
    string DbContextInputFilePath,
    string EntitiesInputDirectoryPath,
    IReadOnlyList<string> EntityInputFilePaths,
    string? InputBackupPath,
    string ContextOutputDirectoryPath,
    string EntitiesOutputDirectoryPath,
    string ConfigurationsOutputDirectoryPath,
    string? ContextNamespace,
    string? EntitiesNamespace,
    string? ConfigurationNamespace)
{
    public string DbContextOutputFilePath => Path.Combine(ContextOutputDirectoryPath, Path.GetFileName(DbContextInputFilePath));
}
