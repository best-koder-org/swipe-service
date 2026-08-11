namespace SwipeService.Services
{
    /// <summary>
    /// Resolves a Keycloak user id (JWT sub claim) to the integer profileId
    /// used internally by the swipe-service.
    /// </summary>
    public interface IUserProfileResolver
    {
        /// <summary>
        /// Resolves the calling user's profile id by calling UserService /api/profiles/me
        /// with the supplied bearer token. Results are cached in-memory per keycloakId.
        /// </summary>
        /// <param name="keycloakId">The user's Keycloak sub claim (UUID string).</param>
        /// <param name="bearerToken">The raw JWT (without the "Bearer " prefix) used to authenticate to UserService.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The profile id, or null when resolution failed.</returns>
        Task<int?> ResolveProfileIdAsync(string keycloakId, string bearerToken, CancellationToken ct = default);
    }
}
