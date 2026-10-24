using CodeSentinel.Data.Entities;

namespace CodeSentinel.Reviews;

/// <summary>
/// 评审单状态机：按审核人结论重算评审单状态。
/// </summary>
public static class ReviewStateMachine
{
    /// <summary>
    /// 按审核人结论重算评审单状态：忽略已移除的审核人；任一要求修改则要求修改；
    /// 非空且全部通过则通过；其余情况评审中。
    /// </summary>
    /// <param name="states">审核人结论集合</param>
    /// <returns>重算后的评审单状态</returns>
    public static ReviewStatus Recompute(IEnumerable<ReviewerState> states)
    {
        var activeStates = states
            .Where(m => m != ReviewerState.Removed)
            .ToList();

        if (activeStates.Contains(ReviewerState.ChangesRequested))
        {
            return ReviewStatus.ChangesRequested;
        }

        if (activeStates.Count > 0 && activeStates.All(m => m == ReviewerState.Approved))
        {
            return ReviewStatus.Approved;
        }

        return ReviewStatus.Open;
    }
}
