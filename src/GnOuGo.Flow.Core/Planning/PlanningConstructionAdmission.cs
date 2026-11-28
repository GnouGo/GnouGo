using GnOuGo.Flow.Core.Runtime;

namespace GnOuGo.Flow.Core.Planning;

/// <summary>Optional host-owned admission for a draft or correction and its mandatory reviews.</summary>
public interface IPlanningConstructionAdmission
{
    Task AdmitConstructionAsync(LLMRequest request, int inputTokenCeiling,
        IReadOnlyList<int> outputTokenCeilings, CancellationToken ct);
}
