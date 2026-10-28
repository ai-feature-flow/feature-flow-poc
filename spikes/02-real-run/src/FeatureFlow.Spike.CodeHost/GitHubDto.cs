using System.Text.Json.Serialization;

namespace FeatureFlow.Spike.CodeHost;

/// <summary>
/// Wire shapes for the GitHub REST API. Internal to the provider: these are the
/// transport, and they never appear in <see cref="ICodeHostProvider"/>
/// signatures, which is what keeps the interface transport-neutral
/// (docs/04 section 4.5).
///
/// Only the fields the provider reads are declared. Unknown JSON is ignored.
/// </summary>
internal static class Dto
{
    public sealed class Ref
    {
        [JsonPropertyName("ref")] public string Name { get; set; } = "";
        [JsonPropertyName("sha")] public string Sha { get; set; } = "";
        [JsonPropertyName("object")] public RefObject? Object { get; set; }
    }

    public sealed class RefObject
    {
        [JsonPropertyName("sha")] public string Sha { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
    }

    public sealed class Commit
    {
        [JsonPropertyName("sha")] public string Sha { get; set; } = "";
        [JsonPropertyName("commit")] public CommitDetail? Detail { get; set; }
        [JsonPropertyName("tree")] public TreeRef? Tree { get; set; }
        [JsonPropertyName("parents")] public List<ParentRef>? Parents { get; set; }
    }

    public sealed class TreeRef
    {
        [JsonPropertyName("sha")] public string Sha { get; set; } = "";
    }

    public sealed class ParentRef
    {
        [JsonPropertyName("sha")] public string Sha { get; set; } = "";
    }

    public sealed class CommitDetail
    {
        [JsonPropertyName("message")] public string Message { get; set; } = "";
    }

    public sealed class PullRequest
    {
        [JsonPropertyName("number")] public int Number { get; set; }
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("merged")] public bool Merged { get; set; }
        [JsonPropertyName("mergeable")] public bool Mergeable { get; set; }
        [JsonPropertyName("mergeable_state")] public string MergeableState { get; set; } = "";
        [JsonPropertyName("merge_commit_sha")] public string? MergeCommitSha { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("head")] public Side? Head { get; set; }
        [JsonPropertyName("base")] public Side? Base { get; set; }
    }

    public sealed class Side
    {
        [JsonPropertyName("ref")] public string Ref { get; set; } = "";
        [JsonPropertyName("sha")] public string Sha { get; set; } = "";
    }

    public sealed class Review
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("user")] public User? User { get; set; }
        [JsonPropertyName("state")] public string State { get; set; } = "";
    }

    public sealed class User
    {
        [JsonPropertyName("login")] public string Login { get; set; } = "";
    }

    public sealed class ReviewCommentDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("body")] public string Body { get; set; } = "";
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("line")] public int? Line { get; set; }
        [JsonPropertyName("original_line")] public int? OriginalLine { get; set; }
        [JsonPropertyName("commit_id")] public string? CommitId { get; set; }
        [JsonPropertyName("original_commit_id")] public string? OriginalCommitId { get; set; }
        [JsonPropertyName("in_reply_to_id")] public long? InReplyToId { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("user")] public User? User { get; set; }
        [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; set; }
    }

    public sealed class CheckRunsResponse
    {
        [JsonPropertyName("total_count")] public int TotalCount { get; set; }
        [JsonPropertyName("check_runs")] public List<CheckRunDto> CheckRuns { get; set; } = new();
    }

    public sealed class CheckRunDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("conclusion")] public string? Conclusion { get; set; }
        [JsonPropertyName("details_url")] public string? DetailsUrl { get; set; }
        [JsonPropertyName("started_at")] public DateTimeOffset? StartedAt { get; set; }
        [JsonPropertyName("completed_at")] public DateTimeOffset? CompletedAt { get; set; }
    }

    public sealed class CombinedStatusResponse
    {
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("total_count")] public int TotalCount { get; set; }
        [JsonPropertyName("statuses")] public List<StatusDto> Statuses { get; set; } = new();
    }

    public sealed class StatusDto
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("context")] public string Context { get; set; } = "";
        [JsonPropertyName("state")] public string State { get; set; } = "";
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("target_url")] public string? TargetUrl { get; set; }
    }

    public sealed class ContentResponse
    {
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("encoding")] public string? Encoding { get; set; }
        [JsonPropertyName("sha")] public string? Sha { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
    }

    public sealed class ProtectionResponse
    {
        [JsonPropertyName("enabled")] public bool? Enabled { get; set; }

        [JsonPropertyName("required_pull_request_reviews")]
        public ProtectionReviews? RequiredReviews { get; set; }

        [JsonPropertyName("required_status_checks")]
        public ProtectionChecks? RequiredChecks { get; set; }

        [JsonPropertyName("enforce_admins")]
        public ProtectionEnforceAdmins? EnforceAdmins { get; set; }

        [JsonPropertyName("required_conversation_resolution")]
        public ProtectionFlag? RequiredConversationResolution { get; set; }
    }

    public sealed class ProtectionReviews
    {
        [JsonPropertyName("required_approving_reviews")]
        public int RequiredApprovingReviews { get; set; }
    }

    public sealed class ProtectionChecks
    {
        [JsonPropertyName("contexts")] public List<string> Contexts { get; set; } = new();
    }

    public sealed class ProtectionEnforceAdmins
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    }

    public sealed class ProtectionFlag
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    }

    public sealed class MergeResponse
    {
        [JsonPropertyName("sha")] public string? Sha { get; set; }
        [JsonPropertyName("merged")] public bool Merged { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
    }

    public sealed class ErrorResponse
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("documentation_url")] public string? DocumentationUrl { get; set; }
    }
}
