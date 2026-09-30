using System.Reflection;
using GitHub.Copilot;

namespace GnOuGo.GithubCopilot.Core.Tests;

public sealed class CopilotAttachmentBoundaryTests
{
    [Fact]
    public void ExistingSdkMappingRetainsFileConfinementReadPolicyAndBlobDefaults()
    {
        var parent = Directory.CreateTempSubdirectory("copilot-attachment-boundary-").FullName;
        try
        {
            var root = Directory.CreateDirectory(Path.Combine(parent, "project")).FullName;
            File.WriteAllText(Path.Combine(root, "readme.md"), "content");
            File.WriteAllText(Path.Combine(parent, "outside.txt"), "outside");
            var policy = new ReadPolicy();
            var file = Assert.IsType<AttachmentFile>(Assert.Single(Map([new("file", "readme.md")], root, policy)!));
            Assert.Equal(Path.Combine(root, "readme.md"), file.Path); Assert.Equal(1, policy.Reads);
            foreach (var path in new[] { "../outside.txt", "missing.txt", root })
                Assert.IsType<UnauthorizedAccessException>(Assert.Throws<TargetInvocationException>(() => Map([new("file", path)], root, policy)).InnerException);
            policy.Refuse = true;
            Assert.IsType<UnauthorizedAccessException>(Assert.Throws<TargetInvocationException>(() => Map([new("file", "readme.md")], root, policy)).InnerException);
            var blob = Assert.IsType<AttachmentBlob>(Assert.Single(Map([new("blob", "display.txt", "aGVsbG8=")], root, policy)!));
            Assert.Equal("application/octet-stream", blob.MimeType); Assert.Equal("display.txt", blob.DisplayName); Assert.Equal("aGVsbG8=", blob.Data);
            Assert.Null(Map(null, root, policy)); Assert.Null(Map([], root, policy));
        }
        finally { Directory.Delete(parent, true); }
    }

    [Fact]
    public void DescriptiveAllowlistEntriesDoNotAuthorizeInstallOrWriteCommands()
    {
        var command = new PermissionRequestShell
        {
            FullCommandText = "npm install", Commands = [new() { Identifier = "npm", ReadOnly = false }],
            CanOfferSessionApproval = false, HasWriteFileRedirection = false, Intention = "fixture",
            PossiblePaths = [], PossibleUrls = []
        };
        Assert.False(GitHubCopilotSdkClient.IsAllowlisted(command, ["install dependencies detected from repository manifests", "run unit tests"]));
        Assert.False(GitHubCopilotSdkClient.IsAllowlisted(command, ["npm"]));
    }

    private static IList<Attachment>? Map(IReadOnlyList<CopilotAttachment>? attachments, string root, ICopilotFileAccessPolicy policy)
        => (IList<Attachment>?)typeof(GitHubCopilotSdkSession).GetMethod("BuildAttachments", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [attachments, root, policy]);

    private sealed class ReadPolicy : ICopilotFileAccessPolicy
    {
        internal bool Refuse; internal int Reads;
        public void ValidateRead(string path) { Reads++; if (Refuse) throw new UnauthorizedAccessException("Refused by host policy"); }
        public void ValidateWrite(string path, string? content = null) => throw new UnauthorizedAccessException();
    }
}
