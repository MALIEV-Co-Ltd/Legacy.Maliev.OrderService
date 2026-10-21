using System.Xml.Linq;

namespace Legacy.Maliev.OrderService.Tests;

/// <summary>Preserves the retained compiler-documentation output boundary.</summary>
public sealed class GeneratedDocumentationIsolationTests
{
    /// <summary>Owned projects generate documentation without a source-root artifact or copy override.</summary>
    /// <param name="projectName">The owned Order project whose compiler documentation output is inspected.</param>
    [Theory]
    [InlineData("Legacy.Maliev.OrderService.Api")]
    [InlineData("Legacy.Maliev.OrderService.Application")]
    [InlineData("Legacy.Maliev.OrderService.Data")]
    [InlineData("Legacy.Maliev.OrderService.Domain")]
    [InlineData("Legacy.Maliev.OrderService.Tests")]
    public void OwnedProjectKeepsGeneratedDocumentationInBuildOutput(string projectName)
    {
        var projectDirectory = Path.Combine(FindRoot(), projectName);
        var projectPath = Path.Combine(projectDirectory, projectName + ".csproj");
        var project = XDocument.Load(projectPath);
        Assert.Empty(project.Descendants("DocumentationFile"));
        Assert.DoesNotContain(project.Descendants("None"), element =>
            ((string?)element.Attribute("Update"))?.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) == true);
        var generation = Assert.Single(project.Descendants("GenerateDocumentationFile"));
        Assert.Equal("true", generation.Value, ignoreCase: true);
        Assert.False(File.Exists(Path.Combine(projectDirectory, projectName + ".xml")), projectPath);
    }

    /// <summary>The actual built test assembly has readable compiler documentation beside its DLL.</summary>
    [Fact]
    public void TestDocumentationIsGeneratedBesideTheExecutingAssembly()
    {
        var assembly = typeof(GeneratedDocumentationIsolationTests).Assembly;
        var path = Path.ChangeExtension(assembly.Location, ".xml");
        Assert.True(File.Exists(path), "Compiler-generated test documentation is absent from build output.");
        var document = XDocument.Load(path);
        Assert.Equal(assembly.GetName().Name, document.Root?.Element("assembly")?.Element("name")?.Value);
        Assert.Contains(document.Descendants("member"), member =>
            (string?)member.Attribute("name") == "T:" + typeof(GeneratedDocumentationIsolationTests).FullName);
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.OrderService.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("Order repository root is unavailable.");
    }
}
