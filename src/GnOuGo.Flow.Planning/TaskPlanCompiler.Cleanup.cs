using GnOuGo.Flow.Core.Planning;

namespace GnOuGo.Flow.Planning;

public sealed partial class TaskPlanCompiler
{
    private static void GuardCleanup(PlanningNode node)
    {
        var required = PlanningValues.And(PlanningValues.ReadGuard(node.Input), node.Expr is null ? null : PlanningValues.ReadGuard(node.Expr));
        if (required is not null) node.If = PlanningValues.And(required, node.If);
    }

}
