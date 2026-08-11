using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SwipeService.Services
{
    /// <summary>
    /// Default <see cref="IUserProfileResolver"/> that calls UserService's
    /// <c>/api/profiles/me</c> endpoint and caches the keycloakId → profileId mapping
    /// in memory. The cache never expires within a process lifetime because the
    /// keycloakId → profileId mapping is immutable.
    /// </summary>
    public sealed class UserProfileResolver : IUserProfileResolver
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<UserProfileResolver> _logger;
        private static readonly ConcurrentDictionary<string, int> _cache = new();

        public UserProfileResolver(HttpClient httpClient, ILogger<UserProfileResolver> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<int?> ResolveProfileIdAsync(string keycloakId, string bearerToken, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(keycloakId))
            {
                return null;
            }

            if (_cache.TryGetValue(keycloakId, out var cached))
            {
                return cached;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/profiles/me");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

                using var response = await _httpClient.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("UserService /api/profiles/me returned {StatusCode} for keycloakId {KeycloakId}",
                        response.StatusCode, keycloakId);
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Object ||
                    !data.TryGetProperty("id", out var idEl) ||
                    !idEl.TryGetInt32(out var profileId))
                {
                    _logger.LogWarning("UserService response missing data.id for keycloakId {KeycloakId}", keycloakId);
                    return null;
                }

                _cache[keycloakId] = profileId;
                return profileId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve profileId for keycloakId {KeycloakId}", keycloakId);
                return null;
            }
        }

        // Test seam — allows unit tests to seed the cache without HTTP.
        internal static void SeedCacheForTest(string keycloakId, int profileId) => _cache[keycloakId] = profileId;
        internal static void ClearCacheForTest() => _cache.Clear();
    }
}
