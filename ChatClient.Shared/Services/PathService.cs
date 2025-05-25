using Microsoft.Extensions.Configuration;

namespace ChatClient.Shared.Services;

/// <summary>
/// Service for handling path operations related to PDD tickets and their images
/// </summary>
public class PathService
{
    private readonly string _baseImagePath;

    public PathService(IConfiguration configuration)
    {
        _baseImagePath = configuration["ImageStorage:BasePath"]
            ?? throw new ArgumentException("Configuration missing ImageStorage:BasePath setting");
    }

    /// <summary>
    /// Gets the directory path for storing images of a specific ticket
    /// </summary>
    /// <param name="ticketId">The ID of the ticket</param>
    /// <returns>The full directory path for the ticket's images</returns>
    public string GetTicketImageDirectoryPath(string ticketId)
    {
        if (string.IsNullOrEmpty(ticketId))
        {
            throw new ArgumentException("Ticket ID cannot be null or empty", nameof(ticketId));
        }

        return Path.Combine(_baseImagePath, ticketId);
    }

    /// <summary>
    /// Gets the path for a specific image of a ticket
    /// </summary>
    /// <param name="ticketId">The ID of the ticket</param>
    /// <param name="imageName">The name of the image file</param>
    /// <returns>The full path to the specific image</returns>
    public string GetTicketImagePath(string ticketId, string imageName)
    {
        if (string.IsNullOrEmpty(imageName))
        {
            throw new ArgumentException("Image name cannot be null or empty", nameof(imageName));
        }

        return Path.Combine(GetTicketImageDirectoryPath(ticketId), imageName);
    }

    /// <summary>
    /// Ensures that the directory for a ticket's images exists
    /// </summary>
    /// <param name="ticketId">The ID of the ticket</param>
    public void EnsureTicketDirectoryExists(string ticketId)
    {
        var directoryPath = GetTicketImageDirectoryPath(ticketId);
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }
    }

    /// <summary>
    /// Gets the folder path for storing images of a specific ticket
    /// This can be used as a replacement for question.GetTicketImageFolderPath(_folderBasedImagesPath)
    /// </summary>
    /// <param name="questionId">The ID of the question/ticket</param>
    /// <returns>The full directory path for the ticket's images</returns>
    public string GetTicketImageFolderPath(string questionId)
    {
        return GetTicketImageDirectoryPath(questionId);
    }
}
