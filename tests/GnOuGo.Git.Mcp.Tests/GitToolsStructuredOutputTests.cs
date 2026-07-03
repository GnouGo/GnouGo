using System.Reflection;
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
}
