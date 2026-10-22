using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using TotalGit.App.Services;
using TotalGit.App.ViewModels;
using TotalGit.App.Views;
using TotalGit.Core.Avatars;
using TotalGit.Core.Git;
using TotalGit.Core.Hosting;
using TotalGit.Core.Hosting.GitHub;

namespace TotalGit.App.Screens;

/// <summary>
/// The scenes: each drives the app as a user would (opening the repository, selecting a commit, opening a dialog) and
/// saves what's on screen as <c>&lt;scene&gt;-&lt;theme&gt;.png</c>.
/// </summary>
internal static class Screens
{
    private static readonly List<Window> Opened = [];
    private static Options _o = null!;
    private static string _theme = "";

    public static async Task RunAsync(Options o)
    {
        _o = o;
        _theme = o.Theme == ThemeVariant.Light ? "light" : "dark";
        Application.Current!.RequestedThemeVariant = o.Theme;
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => Opened.Add(w));

        var avatars = new AvatarService(Path.Combine(AppSettings.DataDirectory, "avatars"));
        var settings = AppSettings.Load();
        var pullRequests = new GitHubProviderFactory(ChainedCredentialSource.Default());
        var shell = new ShellViewModel(avatars, settings, null, pullRequests, workflows: pullRequests);
        var main = new MainWindow { DataContext = shell, Width = o.Width, Height = o.Height };
        main.Show();
        shell.RestoreTabs(null);
        await shell.OpenPathAsync(o.Repo);
        var tab = shell.SelectedTab!;
        await Settle(() => !tab.IsBusy, 30);
        await Settle(() => false, 4); // pull requests, avatars, graph layout

        await Save(main, "main");

        // A commit and one of its files' diffs.
        if (o.Commit is { } rev)
        {
            tab.SelectedSha = Git(o.Repo, "rev-parse", rev);
            await Settle(() => tab.Details is not null, 10);
            await Settle(() => false, 1);
            await Save(main, "commit");
            if (tab.Details is { } details && details.FileTree.Files.FirstOrDefault(f => f.Change.Kind == ChangeKind.Modified) is { } file)
            {
                details.SelectedFile = file;
                await Settle(() => tab.Diff is not null, 10);
                await Settle(() => false, 1);
                await Save(main, "diff");
                tab.DiffMode = DiffViewMode.Split;
                await Settle(() => false, 1);
                await Save(main, "diff-split");
                tab.DiffMode = DiffViewMode.Inline;
                tab.CloseDiffCommand.Execute(null);
            }
        }

        // Uncommitted changes, when the repository has some.
        tab.SelectedSha = CommitInfo.WorkingTreeSha;
        await Settle(() => tab.Staging is not null, 5);
        if (tab.Staging is { } staging)
        {
            staging.Summary = "Tidy the maths helpers";
            await Settle(() => false, 1);
            await Save(main, "staging");
            if (staging.Unstaged.FirstOrDefault(f => f.Change.Kind == ChangeKind.Modified) is { } changed)
            {
                staging.SelectedFile = changed;
                await Settle(() => tab.Diff is not null, 10);
                await Settle(() => false, 1);
                await Save(main, "staging-diff");
                tab.CloseDiffCommand.Execute(null);
            }
        }

        // The pull request's hover card, as the sidebar shows it.
        if (o.Pr is { } cardNumber && tab.Sidebar.CardForPullRequest(cardNumber) is { } card)
        {
            var host = new Window { Width = 420, Height = 360, Content = new PrHoverCard { DataContext = card, Margin = new Thickness(12) } };
            // It's shown in a tooltip, so on the tooltip's background.
            host.Bind(TemplatedControl.BackgroundProperty, host.GetResourceObservable("ToolTipBackground"));
            host.Show();
            await Settle(() => false, 2);
            await Save(host, "hovercard-pr");
            host.Close();
        }

        // Dialogs, through the window that shows them in the app; each is drawn, then closed (cancelled).
        await Dialog(main, "dialog-confirm", () => main.ConfirmAsync("Discard changes", "Discard all uncommitted changes in repo? This can't be undone.",
            ["src/lib/maths.py", "src/lib/strings.py", "NOTES.md"], "Discard"));
        await Dialog(main, "dialog-choose", () => main.ChooseAsync("Push rejected", "The remote has commits you don't have.", null,
            [new DialogChoice("Pull, then push", IsPrimary: true), new DialogChoice("Force push", IsDanger: true), new DialogChoice("Cancel")]));
        await Dialog(main, "dialog-form", () => main.ShowFormAsync(new FormSpec("New branch", "Branches off main.", "Create",
            [FormField.TextBox("Name", "feature/widgets"), FormField.CheckBox("Check it out", true),
             FormField.Choice("Start from", [new FormChoice("main", "The current branch"), new FormChoice("origin/main", "What's on GitHub", IsWarning: true)])])));
        await Dialog(main, "dialog-tabstyle", () => Run(tab.EditTabStyleCommand));
        await Dialog(main, "dialog-branchrules", () => Run(tab.EditBranchRulesCommand, null));
        await Dialog(main, "dialog-cleanup", () => Run(tab.CleanUpBranchesCommand));
        await Dialog(main, "dialog-worktree", () => Run(tab.CreateWorktreeCommand, null));
        await Dialog(main, "dialog-crash", () => (Task)typeof(App).GetMethod("OfferCrashReportAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [main, "System.InvalidOperationException: Sequence contains no elements\n   at TotalGit.App.Foo()"])!);

        // An error, with its Report and Copy details buttons.
        if (Wanted("error-banner"))
        {
            typeof(RepositoryViewModel).GetMethod("ShowError", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(tab, ["Commit 0000000 is not in the loaded history."]);
            await Settle(() => false, 0.5);
            await Save(main, "error-banner");
            tab.DismissBannerCommand.Execute(null);
        }

        if (o.Pr is { } number)
        {
            var list = (IReadOnlyList<PullRequestSummary>)typeof(RepositoryViewModel)
                .GetField("_pullRequests", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(tab)!;
            var pr = list.FirstOrDefault(p => p.Number == number) ?? throw new InvalidOperationException($"PR #{number} isn't open");
            tab.OpenPullRequestCommand.Execute(pr);
            await Settle(() => Opened.OfType<PullRequestWindow>().Any(), 10);
            var review = Opened.OfType<PullRequestWindow>().First();
            review.Width = o.Width;
            review.Height = o.Height;
            await Settle(() => false, 6);
            await Save(review, "review");

            var vm = (PullRequestReviewViewModel)review.DataContext!;
            if ((vm.Files.FirstOrDefault(f => f.Path.EndsWith("maths.py")) ?? vm.Files.FirstOrDefault()) is { } reviewFile)
            {
                vm.SelectedFile = reviewFile;
                await Settle(() => false, 4);
                await Save(review, "review-file");
            }
            await Dialog(review, "dialog-merge", () => Run(vm.MergeCommand));
        }

        // Switching theme with the windows open: everything should repaint in the other theme's colours.
        if (Wanted("switched"))
        {
            var other = o.Theme == ThemeVariant.Light ? ThemeVariant.Dark : ThemeVariant.Light;
            Application.Current.RequestedThemeVariant = other;
            await Settle(() => false, 1);
            _theme = (other == ThemeVariant.Light ? "light" : "dark") + "-from-" + _theme;
            await Save(main, "switched-main");
            if (Opened.OfType<PullRequestWindow>().FirstOrDefault() is { } open) await Save(open, "switched-review");
            _theme = o.Theme == ThemeVariant.Light ? "light" : "dark";
            Application.Current.RequestedThemeVariant = o.Theme;
            await Settle(() => false, 1);
        }

        // The theme button: one click to the other theme, and back.
        if (Wanted("toggled"))
        {
            shell.ToggleThemeCommand.Execute(null);
            await Settle(() => false, 1);
            _theme = (o.Theme == ThemeVariant.Light ? "dark" : "light") + "-from-" + _theme;
            await Save(main, "toggled-main");
            shell.ToggleThemeCommand.Execute(null);
            await Settle(() => false, 1);
            _theme = o.Theme == ThemeVariant.Light ? "light" : "dark";
            await Save(main, "toggled-back");
        }

        // The merge tool, on a file with conflicts: its repository in a tab of its own, as when a merge stops.
        if (o.Conflict is { } conflicted && Wanted("mergetool"))
        {
            await shell.OpenPathAsync(Path.GetDirectoryName(conflicted)!);
            var mergeTab = shell.SelectedTab!;
            await Settle(() => !mergeTab.IsBusy, 30);
            mergeTab.SelectedSha = CommitInfo.WorkingTreeSha;
            await Settle(() => mergeTab.Staging is not null, 5);
            await Save(main, "mergetool-staging");
            if (mergeTab.Staging?.Unstaged.Concat(mergeTab.Staging.Staged).FirstOrDefault(f => f.Change.Kind == ChangeKind.Conflicted) is { } file)
            {
                mergeTab.Staging.SelectedFile = file;
                await Settle(() => mergeTab.MergeTool is not null, 10);
                await Settle(() => false, 2);
                await Save(main, "mergetool");
            }
        }
    }

    private static Task Run(System.Windows.Input.ICommand command, object? arg = null)
    {
        command.Execute(arg);
        return Task.CompletedTask;
    }

    /// <summary>Opens a dialog, draws it once it's shown, and closes it.</summary>
    private static async Task Dialog(Window owner, string name, Func<Task> open)
    {
        if (!Wanted(name)) return;
        var before = Opened.Count;
        _ = open();
        await Settle(() => Opened.Count > before, 10);
        if (Opened.Count == before)
        {
            Console.Error.WriteLine($"{name}: no dialog opened");
            return;
        }
        var dialog = Opened[^1];
        await Settle(() => false, 1);
        await Save(dialog, name);
        dialog.Close();
        await Settle(() => false, 0.3);
    }

    private static bool Wanted(string name) => _o.Only is not { } only || name.StartsWith(only, StringComparison.OrdinalIgnoreCase);

    private static async Task Save(Window window, string name)
    {
        if (!Wanted(name)) return;
        Dispatcher.UIThread.RunJobs();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"{name}: nothing was drawn");
        var path = Path.Combine(_o.Out, $"{name}-{_theme}.png");
#pragma warning disable CS0618 // the replacement overload needs encoder options we don't care about
        frame.Save(path);
#pragma warning restore CS0618
        Console.WriteLine(path);
        await Task.Yield();
    }

    /// <summary>Lets the app run (git, GitHub, layout) until <paramref name="done"/> or the time is up.</summary>
    private static async Task Settle(Func<bool> done, double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until && !done())
        {
            await Task.Delay(100);
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static string Git(string repo, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, WorkingDirectory = repo };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return output;
    }
}
