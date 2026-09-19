namespace GnOuGo.Flow.Core.Runtime;

/// <summary>Scoped, read-only results from the last completed sequential iteration.</summary>
public static class LoopIterationContract
{
    public static string PreviousResultVariable(string stepId) => "_loop_previous_" + stepId;
}
