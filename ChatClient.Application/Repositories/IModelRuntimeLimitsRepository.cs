using ChatClient.Domain.Models;

namespace ChatClient.Application.Repositories;

public interface IModelRuntimeLimitsRepository
{
    Task<IReadOnlyCollection<ModelRuntimeLimits>> GetAllAsync(CancellationToken cancellationToken = default);

    Task SaveAllAsync(List<ModelRuntimeLimits> limits, CancellationToken cancellationToken = default);
}
