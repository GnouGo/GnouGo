using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning.Tests;

public sealed class InitialCommitmentCorrectionTests
{
    private const string Location = "/requirements/outcomes/0/commitments/0";

    [Theory]
    [InlineData("operation", "/operation")]
    [InlineData("port", "/result/port")]
    [InlineData("output", "/result/output")]
    public async Task MissingDeclarationsHavePreciseSemanticDiagnostics(string field, string suffix)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        var requirements = runtime.Proposal.Requirements!;
        var original = requirements.Outcomes[0].Commitments![0];
        requirements.Outcomes[0].Commitments![0] = field switch
        {
            "operation" => original with { Operation = " " },
            "port" => original with { Result = original.Result! with { Port = " " } },
            _ => original with { Result = original.Result! with { Output = " " } }
        };
        var finding = Assert.Single(PlanningCommitments.Validate(state, requirements, true));
        Assert.Equal(Location + suffix, finding.Location); Assert.Contains("missing", finding.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("location", "filePath")]
    [InlineData("résultat},{/路径", "报告路径")]
    public async Task OperationOnlyAndExactUnicodeNamesRemainValidAndCaseSensitive(string port, string output)
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture(port, output);
        var requirements = runtime.Proposal.Requirements!;
        Assert.Empty(PlanningCommitments.Validate(state, requirements, true));
        var original = requirements.Outcomes[0].Commitments![0];
        requirements.Outcomes[0].Commitments![0] = original with { Result = null };
        Assert.Empty(PlanningCommitments.Validate(state, requirements, true));
        requirements.Outcomes[0].Commitments![0] = original with { Result = new(port + " ", output) };
        Assert.Equal(Location + "/result/port", Assert.Single(PlanningCommitments.Validate(state, requirements, true)).Location);
        requirements.Outcomes[0].Commitments![0] = original with { Result = new(port, output + " ") };
        Assert.Equal(Location + "/result/output", Assert.Single(PlanningCommitments.Validate(state, requirements, true)).Location);
        requirements.Outcomes[0].Commitments![0] = original with { Result = new(port.ToUpperInvariant(), output) };
        Assert.Equal(Location + "/result/port", Assert.Single(PlanningCommitments.Validate(state, requirements, true)).Location);
    }

    [Fact]
    public async Task DiagnosticNameListsAreBoundedAndNeverContainResultValues()
    {
        var (runtime, state) = await CommitmentCoverageTests.Fixture();
        var requirements = runtime.Proposal.Requirements!;
        requirements.Outputs = Enumerable.Range(0, 100).Select(i => new TaskInput { Name = $"field{i}_" + new string('x', 1000), Type = new() { Kind = "string" } }).ToList();
        var finding = Assert.Single(PlanningCommitments.Validate(state, requirements, true));
        Assert.InRange(finding.Message.Length, 1, 1400); Assert.Contains("first 8 shown", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("field8_", finding.Message, StringComparison.Ordinal); Assert.DoesNotContain(new string('x', 100), finding.Message, StringComparison.Ordinal);
    }
}
