using System.Reflection;
using System.Text.Json.Nodes;
using GnOuGo.Mcp.Core;
using ModelContextProtocol.Server;
using Xunit;

namespace GnOuGo.Git.Mcp.Tests;

public sealed class GitToolsStructuredOutputTests
{
    [Fact]
    public void AllGitMcpTools_DeclareStructuredOutputSchemas()
    {
        var toolMethods = typeof(GitTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttribute<McpServerToolAttribute>()
            })
            .Where(item => item.Attribute != null)
            .ToArray();

        Assert.NotEmpty(toolMethods);

        foreach (var item in toolMethods)
        {
            Assert.True(item.Attribute!.UseStructuredContent, item.Method.Name);
            Assert.NotNull(item.Attribute.OutputSchemaType);
            Assert.NotEqual(typeof(object), item.Method.ReturnType);
            Assert.Equal(item.Method.ReturnType, item.Attribute.OutputSchemaType);
        }
    }

    [Fact]
    public void GitMcpProjectRootParameters_AreRequiredStrings()
    {
        var parameters = typeof(GitTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters())
            .Where(parameter => string.Equals(parameter.Name, "projectRoot", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(parameters);
        Assert.All(parameters, parameter =>
        {
            Assert.Equal(typeof(string), parameter.ParameterType);
            Assert.False(parameter.HasDefaultValue);
        });
    }

    [Fact]
    public void GitCloneResult_ProjectRootRelativeIsRequiredStringProperty()
    {
        var property = typeof(GitCloneResult).GetProperty(nameof(GitCloneResult.ProjectRootRelative));

        Assert.NotNull(property);
        Assert.Equal(typeof(string), property!.PropertyType);
    }

    [Fact]
    public void GitTools_AdvertiseWorkspaceArtifactContracts()
    {
        var methods = typeof(GitTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null)
            .ToArray();
        var clone = Assert.Single(methods, method =>
            string.Equals(method.GetCustomAttribute<McpServerToolAttribute>()!.Name, "git_clone", StringComparison.Ordinal));
        AssertArtifactMetadata(
            clone,
            McpArtifactContractMetadata.WorkspaceDirectoryProducerProjectRootRelativeJson);

        var consumers = methods
            .Where(method => method.GetParameters().Any(parameter =>
                string.Equals(parameter.Name, "projectRoot", StringComparison.Ordinal)))
            .ToArray();
        Assert.NotEmpty(consumers);
        Assert.All(consumers, method => AssertWorkspaceConsumer(method));

        var compare = Assert.Single(consumers, method =>
            string.Equals(method.GetCustomAttribute<McpServerToolAttribute>()!.Name, "git_compare_refs", StringComparison.Ordinal));
        var compareMetadata = ReadArtifactMetadata(compare);
        var produced = Assert.Single(Assert.IsType<JsonArray>(compareMetadata["produces"]));
        Assert.Equal(McpArtifactContractMetadata.RevisionComparisonFilesKind, produced!["kind"]!.GetValue<string>());
        Assert.Equal("/filesJson", produced["pointer"]!.GetValue<string>());
        Assert.Equal(McpArtifactContractMetadata.MaterializeMode, produced["mode"]!.GetValue<string>());
    }

    private static void AssertArtifactMetadata(MethodInfo method, string expectedGnougoJson)
    {
        var attribute = Assert.Single(method.GetCustomAttributes<McpMetaAttribute>());
        Assert.Equal(McpArtifactContractMetadata.MetaPropertyName, attribute.Name);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedGnougoJson), JsonNode.Parse(attribute.JsonValue!)));
    }

    private static void AssertWorkspaceConsumer(MethodInfo method)
    {
        var artifacts = ReadArtifactMetadata(method);
        Assert.Contains(
            Assert.IsType<JsonArray>(artifacts["consumes"]),
            item => item is JsonObject consume
                    && consume["kind"]?.GetValue<string>() == McpArtifactContractMetadata.WorkspaceDirectoryKind
                    && consume["pointer"]?.GetValue<string>() == "/projectRoot"
                    && consume["required"]?.GetValue<bool>() == true);
    }

    private static JsonObject ReadArtifactMetadata(MethodInfo method)
    {
        var attribute = Assert.Single(method.GetCustomAttributes<McpMetaAttribute>());
        Assert.Equal(McpArtifactContractMetadata.MetaPropertyName, attribute.Name);
        var gnougo = Assert.IsType<JsonObject>(JsonNode.Parse(attribute.JsonValue!));
        return Assert.IsType<JsonObject>(gnougo[McpArtifactContractMetadata.ArtifactsPropertyName]);
    }
}
