using System.ComponentModel.DataAnnotations;

namespace SwipeService.Models
{
    public class SwipeRequest
    {
        /// <summary>
        /// Legacy field. When the request is authenticated via JWT, this value is
        /// ignored and overwritten with the profile id resolved from the JWT
        /// <c>sub</c> claim. Kept for backwards compatibility with internal callers
        /// (e.g. bot-service) that don't carry an end-user JWT.
        /// </summary>
        public int UserId { get; set; }

        /// <summary>
        /// Target user's profile ID as a string. The Flutter client sends this value
        /// as a string (e.g. "3") because the candidate list returns it that way.
        /// The controller parses it to <c>int</c> before passing to the command handler.
        /// Internal callers (bot-service) also send strings.
        /// </summary>
        [Required]
        public string TargetUserId { get; set; } = string.Empty;

        /// <summary>
        /// Legacy boolean flag. Deserialized when the modern <see cref="Direction"/>
        /// field is absent.
        /// </summary>
        public bool IsLike { get; set; }

        /// <summary>
        /// Modern client contract: "like", "pass", "superlike". Case-insensitive.
        /// When present, takes precedence over <see cref="IsLike"/>.
        /// </summary>
        public string? Direction { get; set; }

        /// <summary>
        /// Optional idempotency key for retry safety. If provided, duplicate requests with the same key
        /// will return the original result instead of creating a duplicate swipe.
        /// </summary>
        public string? IdempotencyKey { get; set; }
    }

    public class BatchSwipeRequest
    {
        [Required]
        public int UserId { get; set; }

        [Required]
        public List<SwipeAction> Swipes { get; set; } = new();

        /// <summary>
        /// Optional batch-level idempotency key. If provided, the entire batch will be treated as idempotent.
        /// </summary>
        public string? IdempotencyKey { get; set; }
    }

    public class SwipeAction
    {
        /// <summary>
        /// Target user's profile ID as a string. Parsed to <c>int</c> by the controller.
        /// </summary>
        [Required]
        public string TargetUserId { get; set; } = string.Empty;

        [Required]
        public bool IsLike { get; set; }
    }

    public class SwipeResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public bool IsMutualMatch { get; set; }
        public int MatchId { get; set; }
    }

    public class UserSwipeHistory
    {
        public int UserId { get; set; }
        public List<SwipeRecord> Swipes { get; set; } = new();
        public int TotalSwipes { get; set; }
        public int TotalLikes { get; set; }
        public int TotalPasses { get; set; }
    }

    public class SwipeRecord
    {
        public int Id { get; set; }
        public int TargetUserId { get; set; }
        public bool IsLike { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    public class MatchResult
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public int MatchedUserId { get; set; }
        public DateTime MatchedAt { get; set; }
        public bool IsActive { get; set; } = true;
    }
}
