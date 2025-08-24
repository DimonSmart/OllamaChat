namespace ChatClient.Api.Services;

public static class SemaphoreHelper
{
    public static async Task<T> ExecuteWithSemaphoreAsync<T>(
        SemaphoreSlim semaphore,
        Func<Task<T>> operation,
        ILogger logger,
        string errorMessage)
    {
        try
        {
            await semaphore.WaitAsync();
            return await operation();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, errorMessage);
            throw;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public static async Task ExecuteWithSemaphoreAsync(
        SemaphoreSlim semaphore,
        Func<Task> operation,
        ILogger logger,
        string errorMessage)
    {
        try
        {
            await semaphore.WaitAsync();
            await operation();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, errorMessage);
            throw;
        }
        finally
        {
            semaphore.Release();
        }
    }
}
