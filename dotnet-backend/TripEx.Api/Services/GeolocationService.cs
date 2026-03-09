using TripEx.Api.Models;

namespace TripEx.Api.Services;

/// <summary>
/// IP-based geolocation service
/// </summary>
public class GeolocationService
{
    private readonly HttpClient _httpClient;

    public GeolocationService(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient();
    }

    public async Task<GeoInfo> GetLocationAsync(string? ipAddress)
    {
        var result = new GeoInfo();
        if (string.IsNullOrEmpty(ipAddress) || ipAddress == "127.0.0.1" || ipAddress == "::1")
            return result;

        try
        {
            var response = await _httpClient.GetFromJsonAsync<IpApiResponse>(
                $"http://ip-api.com/json/{ipAddress}?fields=status,country,city,regionName,timezone,lat,lon&lang=he");

            if (response?.Status == "success")
            {
                var parts = new[] { response.City, response.RegionName, response.Country }
                    .Where(p => !string.IsNullOrEmpty(p));
                result.Location = string.Join(", ", parts);
                result.Timezone = response.Timezone ?? "";

                if (!string.IsNullOrEmpty(response.Timezone))
                {
                    try
                    {
                        var tz = TimeZoneInfo.FindSystemTimeZoneById(response.Timezone);
                        var localTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
                        result.LocalTime = localTime.ToString("dddd, d MMMM yyyy, HH:mm");
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Geolocation error: {ex.Message}");
        }

        return result;
    }

    private class IpApiResponse
    {
        public string? Status { get; set; }
        public string? Country { get; set; }
        public string? City { get; set; }
        public string? RegionName { get; set; }
        public string? Timezone { get; set; }
    }
}
