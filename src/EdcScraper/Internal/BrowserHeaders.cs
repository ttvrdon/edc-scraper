namespace EdcScraper.Internal;

/// <summary>
/// Helper to add realistic browser headers to HttpClient instances,
/// preventing detection as an automation tool.
/// </summary>
internal static class BrowserHeaders
{
    /// <summary>
    /// Adds realistic browser headers to an HttpClient to appear as a normal Chrome browser.
    /// </summary>
    public static void AddToBrowserHeaders(HttpClient client)
    {
        // Modern Chrome User-Agent (realistic browser string)
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");

        // Standard browser headers
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        client.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9,cs;q=0.8");
        client.DefaultRequestHeaders.Add("Accept-Encoding", "gzip, deflate, br");
        client.DefaultRequestHeaders.Add("DNT", "1");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Dest", "empty");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Mode", "cors");
        client.DefaultRequestHeaders.Add("Sec-Fetch-Site", "same-site");
        
        // Chrome security headers
        client.DefaultRequestHeaders.Add("Sec-CH-UA", "\"Not A(Brand\";v=\"99\", \"Google Chrome\";v=\"126\", \"Chromium\";v=\"126\"");
        client.DefaultRequestHeaders.Add("Sec-CH-UA-Mobile", "?0");
        client.DefaultRequestHeaders.Add("Sec-CH-UA-Platform", "\"Windows\"");
    }
}
