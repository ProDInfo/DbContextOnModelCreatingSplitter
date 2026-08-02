using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace DbContextOnModelCreatingSplitter.Tests;

public class ProgramTests
{
    [Fact]
    public void BuildNamespaceForSubdirectory_AppendsFolderSegments()
    {
        var ns = PathLayoutService.BuildNamespaceForSubdirectory("Sample.Project", @"Infrastructure\Configurations");
        Assert.Equal("Sample.Project.Infrastructure.Configurations", ns);
    }

    [Fact]
    public void BuildNamespaceForSubdirectory_ReturnsBaseNamespaceForDot()
    {
        var ns = PathLayoutService.BuildNamespaceForSubdirectory("Sample.Project", ".");
        Assert.Equal("Sample.Project", ns);
    }

    [Fact]
    public void RewriteNamespace_ChangesFileScopedNamespace()
    {
        var source = """
            namespace Sample.Data;

            public class BudgetContext {}
            """;

        var rewritten = SourceTransformService.RewriteNamespace(source, "Sample.Output.Contexts");
        Assert.Contains("namespace Sample.Output.Contexts;", Normalize(rewritten));
    }

    [Fact]
    public void ResolveOutputSubdirectoryPath_RejectsParentTraversal()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PathLayoutService.ResolveOutputSubdirectoryPath(@"C:\output", @"..\outside", "--entities-subdir"));
    }

    [Fact]
    public void ResolveOutputSubdirectoryPath_RejectsAbsoluteSubdirectory()
    {
        Assert.Throws<InvalidOperationException>(() =>
            PathLayoutService.ResolveOutputSubdirectoryPath(@"C:\output", @"C:\outside", "--configs-subdir"));
    }

    [Fact]
    public void FilterEntityFilePaths_ExcludesDbContextFileFromEntityCandidates()
    {
        var candidates = new[]
        {
            @"C:\input\BudgetContext.cs",
            @"C:\input\TaskBlueprint.cs",
            @"C:\input\TeamLead.cs"
        };

        var filtered = PathLayoutService.FilterEntityFilePaths(candidates, @"C:\input\BudgetContext.cs");

        Assert.DoesNotContain(@"C:\input\BudgetContext.cs", filtered, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"C:\input\TaskBlueprint.cs", filtered, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"C:\input\TeamLead.cs", filtered, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Main_WithDifferentOutputDir_WritesEntitiesToEntitiesSubdirAndKeepsInputUnchanged()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"DbContextSplitterTests_{Guid.NewGuid():N}");
        var inputRoot = Path.Combine(tempRoot, "Input");
        var entitiesInputDir = Path.Combine(inputRoot, "Models");
        var outputRoot = Path.Combine(tempRoot, "Output");
        Directory.CreateDirectory(entitiesInputDir);

        var dbContextPath = Path.Combine(inputRoot, "BudgetContext.cs");
        var entityPath = Path.Combine(entitiesInputDir, "TaskBlueprint.cs");
        File.WriteAllText(dbContextPath, """
            namespace Sample.Data;

            using Microsoft.EntityFrameworkCore;

            public class BudgetContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<TaskBlueprint>(entity =>
                    {
                        entity.ToTable("TaskBlueprint");
                    });
                }
            }
            """);
        File.WriteAllText(entityPath, """
            namespace Sample.Domain;

            public class TaskBlueprint
            {
                public int Id { get; set; }
            }
            """);

        try
        {
            Program.Main(
            [
                "-c", dbContextPath,
                "--input-dir", entitiesInputDir,
                "-o", outputRoot,
                "--context-subdir", "Contexts",
                "--entities-subdir", "Entities",
                "--configs-subdir", "Configurations",
                "-n", "Sample",
                "-t", "Entity",
                "-s", "Configuration",
                "-B"
            ]);

            Assert.True(File.Exists(Path.Combine(outputRoot, "Entities", "TaskBlueprintEntity.cs")));
            Assert.True(File.Exists(Path.Combine(outputRoot, "Contexts", "BudgetContext.cs")));
            Assert.True(File.Exists(entityPath));
            Assert.False(File.Exists(Path.Combine(entitiesInputDir, "TaskBlueprintEntity.cs")));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Main_WithoutOutputDir_RenamesInputEntitiesAndStillWritesEntitiesSubdirOutput()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"DbContextSplitterTests_{Guid.NewGuid():N}");
        var entitiesInputDir = Path.Combine(tempRoot, "Entities");
        Directory.CreateDirectory(entitiesInputDir);

        var dbContextPath = Path.Combine(tempRoot, "BudgetContext.cs");
        var entityPath = Path.Combine(entitiesInputDir, "TaskBlueprint.cs");
        File.WriteAllText(dbContextPath, """
            namespace Sample.Data;

            using Microsoft.EntityFrameworkCore;

            public class BudgetContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<TaskBlueprint>(entity =>
                    {
                        entity.ToTable("TaskBlueprint");
                    });
                }
            }
            """);
        File.WriteAllText(entityPath, """
            namespace Sample.Domain;

            public class TaskBlueprint
            {
                public int Id { get; set; }
            }
            """);

        try
        {
            Program.Main(
            [
                "-c", dbContextPath,
                "--input-dir", entitiesInputDir,
                "--entities-subdir", "Entities",
                "--configs-subdir", "Configurations",
                //"--context-subdir", ".",
                "-n", "Sample",
                "-t", "Entity",
                "-s", "Configuration",
                "-B"
            ]);

            Assert.True(File.Exists(Path.Combine(tempRoot, "Entities", "TaskBlueprintEntity.cs")), "Expected TaskBlueprintEntity.cs to exist in the Entities directory.");
            Assert.True(File.Exists(Path.Combine(entitiesInputDir, "TaskBlueprintEntity.cs")), "Expected TaskBlueprintEntity.cs to exist in the input Entities directory.");
            Assert.False(File.Exists(entityPath), "Expected TaskBlueprint.cs not exists in the input Entities directory");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ShouldRenameEntityInputFiles_ReturnsTrueWhenInputAndOutputDirectoriesMatch()
    {
        var shouldRenameInput = PathLayoutService.ShouldRenameEntityInputFiles(
            entitiesInputDirectoryPath: @"C:\models",
            entitiesOutputDirectoryPath: @"C:\models");

        Assert.True(shouldRenameInput);
    }

    [Fact]
    public void SplitDbContextSource_UsesProvidedConfigurationNamespaceOverride()
    {
        var source = """
            namespace Sample.Data;

            using Microsoft.EntityFrameworkCore;

            public class SampleContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Customer>(entity =>
                    {
                        entity.ToTable("Customers");
                    });
                }
            }
            """;

        var options = new Options
        {
            ConfigSuffix = "Configuration",
            EntitySuffix = "Entity",
            NoBackup = true
        };

        var result = DbContextSplitService.SplitDbContextSource(
            source,
            options,
            configurationNamespaceOverride: "Sample.Output.Configurations");

        Assert.Contains("using Sample.Output.Configurations;", Normalize(result.UpdatedDbContextSource));
        Assert.Contains("namespace Sample.Output.Configurations;", Normalize(Assert.Single(result.GeneratedFiles).Content));
    }

    [Fact]
    public void GetRenamedEntityFilePath_RenamesCsFileWhenSuffixIsMissing()
    {
        var renameMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TaskBlueprint"] = "TaskBlueprintEntity"
        };

        var renamedPath = EntitySourceFileService.GetRenamedEntityFilePath(
            @"C:\models\TaskBlueprint.cs",
            renameMap,
            "Entity");

        Assert.Equal(@"C:\models\TaskBlueprintEntity.cs", renamedPath);
    }

    [Fact]
    public void GetRenamedEntityFilePath_DoesNotRenameWhenFileAlreadyHasSuffix()
    {
        var renameMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TaskBlueprint"] = "TaskBlueprintEntity"
        };

        var renamedPath = EntitySourceFileService.GetRenamedEntityFilePath(
            @"C:\models\TaskBlueprintEntity.cs",
            renameMap,
            "Entity");

        Assert.Equal(@"C:\models\TaskBlueprintEntity.cs", renamedPath);
    }

    [Fact]
    public void RenameEntitySourcesByPath_OverwritesExistingDestinationFileWhenRenaming()
    {
        var renameMap = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TaskBlueprint"] = "TaskBlueprintEntity"
        };

        var inputSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\models\TaskBlueprint.cs"] = "public class TaskBlueprint { public int Id { get; set; } }",
            [@"C:\models\TaskBlueprintEntity.cs"] = "public class TaskBlueprintEntity { public int LegacyId { get; set; } }"
        };

        var renamed = EntitySourceFileService.RenameEntitySourcesByPath(inputSources, renameMap, "Entity");

        Assert.Single(renamed);
        Assert.True(renamed.TryGetValue(@"C:\models\TaskBlueprintEntity.cs", out var content));
        Assert.Contains("class TaskBlueprintEntity", content);
        Assert.DoesNotContain("LegacyId", content);
    }

    [Fact]
    public void SplitDbContextSource_UsesCustomConfigSuffix()
    {
        var source = """
            namespace Sample.Data;

            using Microsoft.EntityFrameworkCore;

            public class SampleContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Customer>(entity =>
                    {
                        entity.ToTable("Customers");
                    });
                }
            }
            """;

        var options = new Options
        {
            ConfigSuffix = "Map",
            EntitySuffix = "Entity",
            Namespace = "Sample.Configurations",
            NoBackup = true
        };

        var result = DbContextSplitService.SplitDbContextSource(source, options);
        var generatedFile = Assert.Single(result.GeneratedFiles);

        Assert.Equal("CustomerEntityMap.cs", generatedFile.FileName);
        Assert.Contains("class CustomerEntityMap", Normalize(generatedFile.Content));
        Assert.Contains("ApplyConfiguration(new CustomerEntityMap())", Normalize(result.UpdatedDbContextSource));
    }

    [Fact]
    public void SplitDbContextSource_DoesNotReplaceOnModelCreatingWhenNoReplaceOptionIsEnabled()
    {
        var source = """
            namespace Sample.Data;

            using Microsoft.EntityFrameworkCore;

            public class SampleContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Customer>(entity =>
                    {
                        entity.ToTable("Customers");
                    });
                }
            }
            """;

        var options = new Options
        {
            ConfigSuffix = "Configuration",
            EntitySuffix = "Entity",
            Namespace = "Sample.Configurations",
            NoReplacement = true,
            NoBackup = true
        };

        var result = DbContextSplitService.SplitDbContextSource(source, options);

        Assert.Single(result.GeneratedFiles);
        Assert.Contains("Entity<CustomerEntity>(entity =>", Normalize(result.UpdatedDbContextSource));
        Assert.DoesNotContain("ApplyConfiguration(", Normalize(result.UpdatedDbContextSource));
        Assert.DoesNotContain("using Sample.Configurations;", Normalize(result.UpdatedDbContextSource));
    }

    [Fact]
    public void SplitDbContextSource_UsesExistingConfigUsingsFromProvider()
    {
        var source = """
            namespace Sample.Data;

            using Microsoft.EntityFrameworkCore;
            using Sample.Legacy;

            public class SampleContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Customer>(entity =>
                    {
                        entity.ToTable("Customers");
                    });
                }
            }
            """;

        var options = new Options
        {
            ConfigSuffix = "Configuration",
            Namespace = "Sample.Configurations",
            NoBackup = true
        };

        string? Provider(string fileName)
        {
            return fileName == "CustomerConfiguration.cs"
                ? """
                  // Copyright (c) Contoso
                  // Licensed under the MIT License.
                  
                  using Legacy.Tools;

                  namespace Sample.Configurations;

                  public class CustomerConfiguration {}
                  """
                : null;
        }

        var result = DbContextSplitService.SplitDbContextSource(source, options, Provider);
        var generatedFile = Assert.Single(result.GeneratedFiles);

        Assert.True(generatedFile.Content.Contains("using Legacy.Tools;", StringComparison.Ordinal), generatedFile.Content);
        Assert.StartsWith("// Copyright (c) Contoso", generatedFile.Content.TrimStart());
    }

    [Fact]
    public void SplitDbContextSource_ReturnsNoEntitiesWhenNoModelBuilderEntityCallsExist()
    {
        var source = """
            namespace Sample.Data;

            using Microsoft.EntityFrameworkCore;

            public class SampleContext : DbContext
            {
                public virtual DbSet<Customer> Customers { get; set; } = null!;
            }
            """;

        var options = new Options
        {
            EntitySuffix = "Entity",
            ConfigSuffix = "Configuration",
            NoBackup = true
        };

        var result = DbContextSplitService.SplitDbContextSource(source, options);

        Assert.False(result.HasEntityDefinitions);
        Assert.Empty(result.GeneratedFiles);
        Assert.Equal(Normalize(source), Normalize(result.UpdatedDbContextSource));
    }

    [Fact]
    public void SplitDbContextSource_RenamesDbSetTypeWithModelSuffix()
    {
        var source = """
            namespace Sample.Data;

            using Microsoft.EntityFrameworkCore;

            public class SampleContext : DbContext
            {
                public virtual DbSet<TaskBlueprint> TaskBlueprints { get; set; } = null!;

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<TaskBlueprint>(entity =>
                    {
                        entity.ToTable("TaskBlueprints");
                    });
                }
            }
            """;

        var options = new Options
        {
            ConfigSuffix = "Configuration",
            EntitySuffix = "Entity",
            Namespace = "Sample.Configurations",
            NoBackup = true
        };

        var result = DbContextSplitService.SplitDbContextSource(source, options);

        Assert.Contains("DbSet<TaskBlueprintEntity> TaskBlueprints", Normalize(result.UpdatedDbContextSource));
    }

    [Fact]
    public void SplitDbContextSource_PreservesDbContextPrimaryConstructorAndBaseClass()
    {
        var source = """
            namespace Sample.Data;

            using Microsoft.EntityFrameworkCore;

            public interface ICurrentUserService {}

            public class DbContextSoftDelete : DbContext
            {
                public DbContextSoftDelete(DbContextOptions options, ICurrentUserService currentUserService) {}
            }

            public partial class BudgetContext(DbContextOptions<BudgetContext> options, ICurrentUserService currentUserService) : DbContextSoftDelete(options, currentUserService)
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<TaskBlueprint>(entity =>
                    {
                        entity.ToTable("TaskBlueprints");
                    });
                }
            }

            public class TaskBlueprint
            {
                public int Id { get; set; }
            }
            """;

        var options = new Options
        {
            ConfigSuffix = "Configuration",
            Namespace = "Sample.Configurations",
            NoBackup = true
        };

        var result = DbContextSplitService.SplitDbContextSource(source, options);
        var normalized = Normalize(result.UpdatedDbContextSource);

        Assert.Contains(
            "public partial class BudgetContext(DbContextOptions<BudgetContext> options, ICurrentUserService currentUserService) : DbContextSoftDelete(options, currentUserService)",
            normalized);
        Assert.Contains("ApplyConfiguration(new TaskBlueprintConfiguration())", normalized);
    }

    [Fact]
    public void SplitDbContextSource_AppliesModelSuffixInConfigurationAndDbSet()
    {
        var source = """
            namespace Sample.Data;

            using Microsoft.EntityFrameworkCore;

            public class SampleContext : DbContext
            {
                public virtual DbSet<Customer> Customers { get; set; } = null!;

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Sample.Domain.Customer>(entity =>
                    {
                        entity.ToTable("Customers");
                        entity.HasKey(e => e.Id);
                    });
                }
            }
            """;

        var options = new Options
        {
            ConfigSuffix = "Configuration",
            EntitySuffix = "Entity",
            Namespace = "Sample.Configurations",
            NoBackup = true
        };

        var result = DbContextSplitService.SplitDbContextSource(source, options);
        var generatedFile = Assert.Single(result.GeneratedFiles);

        var expectedConfiguration = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Sample.Configurations;

            public class CustomerEntityConfiguration : IEntityTypeConfiguration<Sample.Domain.CustomerEntity>
            {
                public void Configure(EntityTypeBuilder<Sample.Domain.CustomerEntity> entity)
                {
                    entity.ToTable("Customers");
                    entity.HasKey(e => e.Id);
                }
            }
            """;

        var expectedUpdatedContext = """
            using Sample.Configurations;
            
            namespace Sample.Data;
            
            using Microsoft.EntityFrameworkCore;
            
            public class SampleContext : DbContext
            {
                public virtual DbSet<CustomerEntity> Customers { get; set; } = null!;
            
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new CustomerEntityConfiguration());
                }
            }
            """;

        Assert.True(result.HasEntityDefinitions);
        Assert.Equal("CustomerEntityConfiguration.cs", generatedFile.FileName);
        Assert.Equal(Normalize(expectedConfiguration), Normalize(generatedFile.Content));
        Assert.Equal(Normalize(expectedUpdatedContext), Normalize(result.UpdatedDbContextSource));
    }

    [Fact]
    public void SplitDbContextSource_UsesOriginalModelNameWhenModelSuffixIsMissing()
    {
        var source = """
            namespace Sample.Data;

            using Microsoft.EntityFrameworkCore;

            public class SampleContext : DbContext
            {
                public virtual DbSet<Customer> Customers { get; set; } = null!;

                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Customer>(entity =>
                    {
                        entity.ToTable("Customers");
                    });
                }
            }
            """;

        var options = new Options
        {
            ConfigSuffix = "Configuration",
            Namespace = "Sample.Configurations",
            NoBackup = true
        };

        var result = DbContextSplitService.SplitDbContextSource(source, options);
        var generatedFile = Assert.Single(result.GeneratedFiles);

        var expectedConfiguration = """
            using Microsoft.EntityFrameworkCore;
            using Microsoft.EntityFrameworkCore.Metadata.Builders;

            namespace Sample.Configurations;

            public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
            {
                public void Configure(EntityTypeBuilder<Customer> entity)
                {
                    entity.ToTable("Customers");
                }
            }
            """;

        var expectedUpdatedContext = """
            using Sample.Configurations;
            
            namespace Sample.Data;
            
            using Microsoft.EntityFrameworkCore;
            
            public class SampleContext : DbContext
            {
                public virtual DbSet<Customer> Customers { get; set; } = null!;
            
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.ApplyConfiguration(new CustomerConfiguration());
                }
            }
            """;

        Assert.True(result.HasEntityDefinitions);
        Assert.Equal("CustomerConfiguration.cs", generatedFile.FileName);
        Assert.Equal(Normalize(expectedConfiguration), Normalize(generatedFile.Content));
        Assert.Equal(Normalize(expectedUpdatedContext), Normalize(result.UpdatedDbContextSource));
    }

    [Fact]
    public void RenameEntitySource_RenamesClassAndEntityTypeReferences()
    {
        var source = """
            using System;
            using System.Collections.Generic;

            namespace PresupuestosModule.Domain;

            public partial class TaskBlueprint
            {
                public Guid Uid { get; set; }

                public int PriorityLevel { get; set; }

                public string Title { get; set; } = null!;

                public virtual TeamLead AssignedLead { get; set; } = null!;

                public virtual ICollection<ChecklistNote> ChecklistNotes { get; set; } = new List<ChecklistNote>();
            }
            """;

        var entitySources = new[]
        {
            source,
            "namespace PresupuestosModule.Domain; public partial class TeamLead {}",
            "namespace PresupuestosModule.Domain; public partial class ChecklistNote {}"
        };

        var renameMap = EntitySourceFileService.BuildEntityTypeRenameMap(entitySources, "Entity");
        var renamed = EntitySourceFileService.RenameEntitySource(source, renameMap);

        var expected = """
            using System;
            using System.Collections.Generic;

            namespace PresupuestosModule.Domain;

            public partial class TaskBlueprintEntity
            {
                public Guid Uid { get; set; }

                public int PriorityLevel { get; set; }

                public string Title { get; set; } = null!;

                public virtual TeamLeadEntity AssignedLead { get; set; } = null!;

                public virtual ICollection<ChecklistNoteEntity> ChecklistNotes { get; set; } = new List<ChecklistNoteEntity>();
            }
            """;

        Assert.Equal(Normalize(expected), Normalize(renamed));
    }

    private static string Normalize(string source)
    {
        return CSharpSyntaxTree.ParseText(source).GetRoot().NormalizeWhitespace().ToFullString();
    }
}
