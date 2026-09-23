using TotalGit.Core.Git;
using TotalGit.Core.Graph;

namespace TotalGit.Core.Tests;

public class GraphLayoutBuilderTests
{
    /// <summary>A deterministic DAG with interleaved branches and merges, children before parents.</summary>
    private static List<CommitInfo> SyntheticHistory(int count)
    {
        var rng = new Random(42);
        var shas = Enumerable.Range(0, count).Select(i => $"c{i:D4}").ToArray();
        var commits = new List<CommitInfo>();
        for (var i = 0; i < count; i++)
        {
            var parents = new List<string>();
            if (i < count - 1) parents.Add(shas[i + 1 + rng.Next(Math.Min(3, count - i - 1))]);
            if (i < count - 5 && rng.NextDouble() < 0.2) parents.Add(shas[i + 2 + rng.Next(3)]);
            commits.Add(new CommitInfo(shas[i], parents.Distinct().ToArray(), "A", "a@x", DateTimeOffset.UnixEpoch, shas[i]));
        }
        return commits;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(50)]
    public void Paged_append_matches_single_compute(int pageSize)
    {
        var history = SyntheticHistory(300);
        var expected = GraphLayout.Compute(history);

        var builder = new GraphLayoutBuilder();
        foreach (var page in history.Chunk(pageSize)) builder.Append(page);
        var actual = builder.ToResult();

        Assert.Equal(expected.LaneCount, actual.LaneCount);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (var i = 0; i < expected.Rows.Count; i++)
        {
            Assert.Equal(expected.Rows[i].Lane, actual.Rows[i].Lane);
            Assert.Equal(expected.Rows[i].ColorIndex, actual.Rows[i].ColorIndex);
            Assert.Equal(expected.Rows[i].Segments, actual.Rows[i].Segments);
        }
    }

    [Fact]
    public void Snapshot_is_not_affected_by_later_appends()
    {
        var history = SyntheticHistory(20);
        var builder = new GraphLayoutBuilder();
        builder.Append(history.Take(10));
        var first = builder.ToResult();
        builder.Append(history.Skip(10));

        Assert.Equal(10, first.Rows.Count);
        Assert.Equal(20, builder.RowCount);
    }
}
