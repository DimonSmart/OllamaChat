using ChatClient.Domain.Models;

namespace ChatClient.Application.Services;

public interface ICompactionBudgetResolver
{
    Task<CompactionBudget> ResolveAsync(CompactionProfile profile, ServerModel model);
}
