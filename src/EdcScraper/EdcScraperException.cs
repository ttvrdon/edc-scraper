namespace EdcScraper;

/// <summary>
/// Thrown when the EDC scraper encounters an API or authentication error.
/// </summary>
public sealed class EdcScraperException : Exception
{
    public EdcScraperException(string message) : base(message) { }
    public EdcScraperException(string message, Exception inner) : base(message, inner) { }
}
