using TotalGit.Core.Avatars;

namespace TotalGit.Core.Tests;

public sealed class CustomAvatarsTests : IDisposable
{
    private static readonly byte[] Png = [9, 9, 9];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "totalgit-tests", "custom-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Finds_a_picture_by_email_or_login_ignoring_case()
    {
        var store = new CustomAvatars(_dir);
        var set = store.Set(null, "Ann", ["Ann@Example.com"], ["AnnGH"], Png);

        Assert.Equal(set.Id, store.Find("ann@example.com", null)?.Id);
        Assert.Equal(set.Id, store.Find(null, "anngh")?.Id);
        Assert.Null(store.Find("bob@example.com", "bob"));
        Assert.Equal(Png, File.ReadAllBytes(store.ImagePath(set)));
    }

    [Fact]
    public void Learned_links_carry_a_picture_from_an_email_to_its_login_and_back()
    {
        var store = new CustomAvatars(_dir);
        store.Link("ann@example.com", "ann");
        var byEmail = store.Set(null, "Ann", ["ann@example.com"], [], Png);
        Assert.Equal(byEmail.Id, store.Find(null, "Ann")?.Id);

        store.Link("bob@example.com", "bob");
        var byLogin = store.Set(null, "Bob", [], ["bob"], Png);
        Assert.Equal(byLogin.Id, store.Find("BOB@example.com", null)?.Id);
        Assert.Equal(["bob@example.com"], store.EmailsFor("bob"));
    }

    [Fact]
    public void Setting_again_replaces_the_picture_and_bumps_its_version()
    {
        var store = new CustomAvatars(_dir);
        var first = store.Set(null, "Ann", ["ann@example.com"], [], Png);
        var second = store.Set(first.Id, "Ann", ["ann@example.com", "ann@work.com"], [], [1, 2]);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Version);
        Assert.Single(store.All);
        Assert.Equal([1, 2], File.ReadAllBytes(store.ImagePath(second)));
    }

    [Fact]
    public void Addresses_move_to_the_new_picture_and_emptied_ones_are_dropped()
    {
        var store = new CustomAvatars(_dir);
        var old = store.Set(null, "Ann", ["ann@example.com"], [], Png);
        var merged = store.Set(null, "Ann", ["ann@example.com"], ["ann"], Png);

        Assert.Equal([merged.Id], store.All.Select(a => a.Id));
        Assert.False(File.Exists(store.ImagePath(old)));
    }

    [Fact]
    public void Remove_deletes_the_picture()
    {
        var store = new CustomAvatars(_dir);
        var set = store.Set(null, "Ann", ["ann@example.com"], [], Png);
        var changes = 0;
        store.Changed += () => changes++;

        store.Remove(set.Id);

        Assert.Null(store.Find("ann@example.com", null));
        Assert.False(File.Exists(store.ImagePath(set)));
        Assert.Equal(1, changes);
    }

    [Fact]
    public void Survives_a_reload()
    {
        var set = new CustomAvatars(_dir).Set(null, "Ann", ["ann@example.com"], [], Png, presetId: "fox");
        new CustomAvatars(_dir).Link("ann@example.com", "ann");

        var reloaded = new CustomAvatars(_dir);
        var found = reloaded.Find(null, "ann");
        Assert.Equal((set.Id, "Ann", 1, "fox"), (found?.Id, found?.Name, found?.Version, found?.PresetId));
        Assert.Equal(["ann@example.com"], found!.Emails);
        Assert.Equal("ann", reloaded.LoginFor("ann@example.com"));
    }

    [Fact]
    public void A_corrupt_file_gives_an_empty_store()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "people.json"), "{ not json");

        Assert.Empty(new CustomAvatars(_dir).All);
    }

    [Fact]
    public void A_picture_needs_someone_to_belong_to() =>
        Assert.Throws<ArgumentException>(() => new CustomAvatars(_dir).Set(null, "Nobody", [" "], [], Png));
}
