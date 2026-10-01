using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.OrderService.Tests;

public sealed class PublishWorkflowPermissionContractTests
{
    private const string Publisher = "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/.github/workflows/publish-image.yml@503e8846390a597c267d2889b33a9c26863389b3";

    [Fact]
    public void Publisher_UsesReviewedImmutableValidationProducer()
    {
        var publish = Publish(Parse(Source()));
        Assert.Equal(Publisher, Scalar(publish, "uses"));
    }

    [Fact]
    public void Publisher_GrantsOnlyThreeRequiredJobPermissionsAndRetainsDormantInputs()
    {
        ValidatePermissionsAndInputs(Parse(Source()));
    }

    [Theory]
    [InlineData("contents", "write")]
    [InlineData("actions", "write")]
    [InlineData("id-token", "read")]
    [InlineData("actions", "")]
    [InlineData("contents", "")]
    [InlineData("id-token", "")]
    [InlineData("packages", "write")]
    public void Publisher_RejectsMissingElevatedOrAdditionalPermission(string name, string value)
    {
        var root = Parse(Source());
        ValidatePermissionsAndInputs(root);
        var permissions = Mapping(Publish(root), "permissions");
        if (value.Length == 0) permissions.Children.Remove(new YamlScalarNode(name));
        else permissions.Children[new YamlScalarNode(name)] = new YamlScalarNode(value);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ValidatePermissionsAndInputs(root));
    }

    private static void ValidatePermissionsAndInputs(YamlMappingNode root)
    {
        var workflowPermissions = Mapping(root, "permissions");
        Assert.Single(workflowPermissions.Children);
        Assert.Equal("read", Scalar(workflowPermissions, "contents"));
        var publish = Publish(root);
        var permissions = Mapping(publish, "permissions");
        Assert.Equal(3, permissions.Children.Count);
        Assert.Equal("read", Scalar(permissions, "contents"));
        Assert.Equal("read", Scalar(permissions, "actions"));
        Assert.Equal("write", Scalar(permissions, "id-token"));
        Assert.Equal("vars.LEGACY_DEPLOY_ENABLED == 'true'", Scalar(publish, "if"));
        var gate = Mapping(Mapping(root, "jobs"), "deployment-gate");
        Assert.Equal("vars.LEGACY_DEPLOY_ENABLED != 'true'", Scalar(gate, "if"));
        Assert.False(gate.Children.ContainsKey(new YamlScalarNode("permissions")));
        var inputs = Mapping(publish, "with");
        Assert.Equal(6, inputs.Children.Count);
        Assert.Equal("${{ vars.LEGACY_ARTIFACT_REGISTRY }}/legacy-maliev-order-service", Scalar(inputs, "image"));
        Assert.Equal("Legacy.Maliev.OrderService.Api/Dockerfile", Scalar(inputs, "dockerfile"));
        Assert.Equal(".", Scalar(inputs, "context"));
        Assert.Equal("legacy-production", Scalar(inputs, "environment"));
        Assert.Equal("${{ vars.LEGACY_WORKLOAD_IDENTITY_PROVIDER }}", Scalar(inputs, "workload-identity-provider"));
        Assert.Equal("${{ vars.LEGACY_ORDER_PUBLISHER_SERVICE_ACCOUNT }}", Scalar(inputs, "service-account"));
    }

    private static string Source() => File.ReadAllText(Path.Combine(FindRoot(), ".github", "workflows", "publish-image.yml"));
    private static YamlMappingNode Parse(string source)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(source));
        return Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
    }
    private static YamlMappingNode Mapping(YamlMappingNode parent, string name) =>
        Assert.IsType<YamlMappingNode>(parent.Children[new YamlScalarNode(name)]);
    private static YamlMappingNode Publish(YamlMappingNode root) => Mapping(Mapping(root, "jobs"), "publish");
    private static string? Scalar(YamlMappingNode parent, string name) =>
        parent.Children.TryGetValue(new YamlScalarNode(name), out var node)
            ? Assert.IsType<YamlScalarNode>(node).Value : null;

    [Fact]
    public void PublishWorkflow_ScopesOidcToPublishJobs()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRoot(),
            ".github",
            "workflows",
            "publish-image.yml"));

        var jobsIndex = source.IndexOf("\njobs:", StringComparison.Ordinal);
        Assert.True(jobsIndex > 0, "The publish workflow must define a jobs section.");
        var workflowHeader = source[..jobsIndex];
        Assert.Contains("permissions:", workflowHeader, StringComparison.Ordinal);
        Assert.Contains("  contents: read", workflowHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("id-token: write", workflowHeader, StringComparison.OrdinalIgnoreCase);

        var publishJobs = Regex.Matches(
            source,
            @"(?ms)^  publish(?:-[^:\r\n]+)?:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*\r?$|\z)");
        Assert.NotEmpty(publishJobs);
        foreach (Match publishJob in publishJobs)
        {
            var job = publishJob.Groups["body"].Value;
            Assert.Contains("permissions:", job, StringComparison.Ordinal);
            Assert.Contains("contents: read", job, StringComparison.Ordinal);
            Assert.Contains("id-token: write", job, StringComparison.Ordinal);
        }

        var deploymentGate = Regex.Match(
            source,
            @"(?ms)^  deployment-gate:\r?\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:\s*\r?$|\z)");
        if (deploymentGate.Success)
        {
            Assert.DoesNotContain("id-token: write", deploymentGate.Groups["body"].Value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var workflow = Path.Combine(directory.FullName, ".github", "workflows", "publish-image.yml");
            if (File.Exists(workflow))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The publish workflow root was not found.");
    }
}
