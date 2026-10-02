using System.Text.Json;

namespace TotalGit.Core.Hosting;

/// <summary>
/// Where the signed-in user marked a pull request's file as reviewed: the pull request head at the time, and the
/// file's content then (its blob), so a later change to the file can be told apart from other files' changes.
/// </summary>
public sealed record ReviewMark(string HeadSha, string? BlobSha, DateTimeOffset MarkedAt);

/// <summary>How far the signed-in user has reviewed one file of a pull request.</summary>
public enum FileReviewState
{
    NotReviewed,

    /// <summary>Reviewed, then the file changed: part reviewed.</summary>
    ChangedSinceReview,
    Reviewed,
}

/// <summary>
/// The reviewed marks kept on this machine, per pull request and file (a JSON file). The host's own "viewed" mark is
/// the main record; these add the commit each file was reviewed at, which the host doesn't say, so the lines changed
/// since can be shown.
/// </summary>
public sealed class ReviewMarks(string filePath)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private Dictionary<string, Dictionary<string, ReviewMark>>? _marks;

    /// <summary>The key of one pull request, e.g. <c>github.com/octo/widgets#12</c>.</summary>
    public static string Key(RemoteHostInfo host, int number) => $"{host.Host}/{host.Owner}/{host.Repo}#{number}".ToLowerInvariant();

    public ReviewMark? Get(string pullRequest, string path)
    {
        lock (_lock) return Marks().GetValueOrDefault(pullRequest)?.GetValueOrDefault(path);
    }

    public IReadOnlyDictionary<string, ReviewMark> All(string pullRequest)
    {
        lock (_lock) return Marks().TryGetValue(pullRequest, out var files) ? new Dictionary<string, ReviewMark>(files) : new Dictionary<string, ReviewMark>();
    }

    public void Mark(string pullRequest, string path, ReviewMark mark)
    {
        lock (_lock)
        {
            var marks = Marks();
            if (!marks.TryGetValue(pullRequest, out var files)) marks[pullRequest] = files = [];
            files[path] = mark;
            Save();
        }
    }

    public void Unmark(string pullRequest, string path)
    {
        lock (_lock)
        {
            var marks = Marks();
            if (!marks.TryGetValue(pullRequest, out var files) || !files.Remove(path)) return;
            if (files.Count == 0) marks.Remove(pullRequest);
            Save();
        }
    }

    /// <summary>
    /// A file's review state from the host's viewed mark and the local one: reviewed while the file is as it was
    /// when marked; part reviewed once it has changed (or the host says it changed).
    /// </summary>
    public static FileReviewState StateOf(FileViewState host, ReviewMark? local, string? currentBlob)
    {
        if (host == FileViewState.ChangedSinceViewed) return FileReviewState.ChangedSinceReview;
        if (host == FileViewState.Viewed)
            return local?.BlobSha is { } blob && currentBlob is not null && blob != currentBlob ? FileReviewState.ChangedSinceReview : FileReviewState.Reviewed;
        // Not viewed on the host: a local mark alone (e.g. a host without viewed marks) still counts.
        if (local is null) return FileReviewState.NotReviewed;
        return local.BlobSha is { } b && currentBlob is not null && b != currentBlob ? FileReviewState.ChangedSinceReview : FileReviewState.Reviewed;
    }

    private Dictionary<string, Dictionary<string, ReviewMark>> Marks()
    {
        if (_marks is not null) return _marks;
        try
        {
            _marks = File.Exists(filePath)
                ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, ReviewMark>>>(File.ReadAllText(filePath)) ?? []
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            _marks = [];
        }
        return _marks;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, JsonSerializer.Serialize(_marks, JsonOptions));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Marks are a convenience; the host keeps the viewed state itself.
        }
    }
}
