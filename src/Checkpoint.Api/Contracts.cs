namespace Checkpoint.Api;

public sealed record CommunityState(long TotalXp, string Revision);
public sealed record VoteRequest(Guid PollId, Guid OptionId);
public sealed record ImageData(string MediaType, int Width, int Height, string Base64);
public sealed record PollOption(Guid Id, string Label);
public sealed record AssignedPoll(Guid Id, string Title, PollOption[] Options, Guid? AnswerOptionId, bool CanVote);
public sealed record ParticipationResponse(string Status, int RequiredPollCount, int AssignedPollCount,
    int AnsweredPollCount, bool ResultsUnlocked, string PhotoStatus, AssignedPoll[] Polls);
public sealed record ResultOption(Guid Id, string Label, long Votes);
public sealed record PollResult(Guid PollId, string Title, long TotalVotes, ResultOption[] Options);
public sealed record ResultsResponse(DateTime AsOfUtc, PollResult[] Results);
public sealed record ErrorDetails(string Code, string Message, string TraceId);
public sealed record ErrorResponse(ErrorDetails Error);
public sealed record HealthResponse(string Status);
public sealed record ArtworkCharacter(string Role, string MediaType, int Width, int Height, string Base64);
public sealed record CurrentLevelResponse(string Id, string Name, int MinimumXp, int? NextLevelAtXp, bool IsFinal, string ArtVersion, ArtworkCharacter[] Characters);
public sealed record PreviousLevelResponse(string Id, string Name, int MinimumXp);
public sealed record ProgressResponse(long TotalXp, string Revision, DateTime UpdatedAtUtc, CurrentLevelResponse CurrentLevel, PreviousLevelResponse[] PreviousLevels);
public sealed record VoteResponse(Guid PollId, Guid OptionId, int AwardedXp, int AnsweredPollCount, int RequiredPollCount, bool ResultsUnlocked, CommunityState Community);
public sealed record PhotoResponse(Guid PhotoId, string PhotoStatus, int AwardedXp, CommunityState Community);
public sealed record RecentPhoto(Guid Id, DateTime CreatedAtUtc, string Alt, ImageData Image);
public sealed record RecentPhotosResponse(DateTime UpdatedAtUtc, RecentPhoto[] Photos);
public sealed record PhotoUploadRequest(IFormFile File);
public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
