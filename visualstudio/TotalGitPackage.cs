using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EnvDTE;
using Microsoft;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using TotalGit.VisualStudio.Git;
using TotalGit.VisualStudio.UI;
using Task = System.Threading.Tasks.Task;

namespace TotalGit.VisualStudio;

/// <summary>
/// Total Git for Visual Studio: a Worktrees window to see the open solution's worktrees and switch between them,
/// and commands that hand things to Total Git.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("Total Git", "Worktrees in Visual Studio, linked to Total Git.", "0.3")]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(WorktreesWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids.SolutionExplorer)]
[ProvideOptionPage(typeof(OptionsPage), "Total Git", "General", 0, 0, true)]
// Loaded once a solution is open, so it can tell which files are conflicted before their menus open.
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
[Guid(PackageGuidString)]
public sealed class TotalGitPackage : AsyncPackage
{
    public const string PackageGuidString = "fa889656-5f5d-464b-87a7-16c23c7bbf28";
    public static readonly Guid CommandSet = new("cdbe376f-73e5-4954-a8ba-4929f55c0090");

    private const int ShowWorktreesId = 0x0100;
    private const int SwitchWorktreeId = 0x0101;
    private const int OpenRepoInTotalGitId = 0x0102;
    private const int ResolveInTotalGitId = 0x0103;
    private const int RefreshId = 0x0104;
    private const int CtxSwitchId = 0x0200;
    private const int CtxNewWindowId = 0x0201;
    private const int CtxOpenInTotalGitId = 0x0202;
    private const int CtxOpenInVsCodeId = 0x0203;
    private const int CtxRevealId = 0x0204;
    private const int CtxCopyPathId = 0x0205;
    internal const int ToolbarId = 0x1001;
    private const int WorktreeMenuId = 0x1002;

    private const string LastSolutionsCollection = @"TotalGit\LastSolutions";

    private DTE2? _dte;
    private IVsSolution? _solution;
    private OleMenuCommandService? _commands;
    private DocumentEvents? _documentEvents; // held so its handlers stay attached

    internal WorktreeModel Model { get; } = new();

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        _dte = await GetServiceAsync(typeof(DTE)) as DTE2;
        _solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
        _commands = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        Assumes.Present(_dte);
        Assumes.Present(_solution);
        Assumes.Present(_commands);

        Model.Changed += (_, _) => JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            (FindToolWindow(typeof(WorktreesWindow), 0, false) as WorktreesWindow)?.Control?.Show();
        }).FileAndForget("TotalGit/ShowWorktrees");

        AddCommands();

        Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenSolution += (_, _) => Model.SetOpenFolder(OpenFolder());
        Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseSolution += (_, _) => Model.SetOpenFolder(OpenFolder());
        Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterOpenFolder += (_, _) => Model.SetOpenFolder(OpenFolder());
        Microsoft.VisualStudio.Shell.Events.SolutionEvents.OnAfterCloseFolder += (_, _) => Model.SetOpenFolder(OpenFolder());
        // Back from another app (a commit or checkout made elsewhere), and after saving files: counts change.
        if (System.Windows.Application.Current is { } app) app.Activated += (_, _) => Model.RefreshSoon(0);
        if (_dte?.Events.DocumentEvents is { } documentEvents)
        {
            _documentEvents = documentEvents;
            _documentEvents.DocumentSaved += _ => Model.RefreshSoon(1500);
        }

        Model.SetOpenFolder(OpenFolder());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Model.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>The folder of the open solution, or the open folder; null with nothing open.</summary>
    private string? OpenFolder()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_solution is null) return null;
        _solution.GetSolutionInfo(out var dir, out var file, out _);
        if (!string.IsNullOrEmpty(file)) return Path.GetDirectoryName(file);
        return string.IsNullOrEmpty(dir) ? null : dir;
    }

    private bool InFolderMode()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return _solution is not null
               && ErrorHandler.Succeeded(_solution.GetProperty((int)__VSPROPID7.VSPROPID_IsInOpenFolderMode, out var value))
               && value is true;
    }

    // ------------------------------------------------------------------ commands

    private void AddCommands()
    {
        if (_commands is null) return;
        Add(ShowWorktreesId, () => ShowToolWindowAsync(typeof(WorktreesWindow), 0, true, DisposalToken));
        Add(SwitchWorktreeId, SwitchWorktreeAsync);
        AddSync(OpenRepoInTotalGitId, () =>
        {
            var folder = Model.CurrentEntry?.Folder ?? OpenFolder();
            if (folder is null) ShowStatus("Open a solution or folder in a git repository first.");
            else OpenInTotalGit(folder);
        });
        var resolve = Add(ResolveInTotalGitId, ResolveConflictsAsync);
        resolve.BeforeQueryStatus += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            resolve.Visible = resolve.Enabled = SelectedFiles().Any(Model.IsConflicted);
        };
        Add(RefreshId, () => Model.RefreshAsync());

        var ctxSwitch = Add(CtxSwitchId, () => SelectedWorktree() is { } e ? OpenWorktreeAsync(e, false) : Task.CompletedTask);
        ctxSwitch.BeforeQueryStatus += (_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ctxSwitch.Visible = SelectedWorktree() is { Current: false };
        };
        Add(CtxNewWindowId, () => SelectedWorktree() is { } e ? OpenWorktreeAsync(e, true) : Task.CompletedTask);
        AddSync(CtxOpenInTotalGitId, () => OnSelected(e => OpenInTotalGit(e.Folder)));
        AddSync(CtxOpenInVsCodeId, () => OnSelected(e =>
        {
            if (!ExternalApps.OpenInVsCode(e.Folder)) ShowStatus("VS Code wasn't found.");
        }));
        AddSync(CtxRevealId, () => OnSelected(e =>
            System.Diagnostics.Process.Start(new ProcessStartInfo("explorer.exe", GitRunner.Quote(e.Folder)))?.Dispose()));
        AddSync(CtxCopyPathId, () => OnSelected(e =>
        {
            System.Windows.Clipboard.SetText(e.Folder);
            ShowStatus("Copied " + e.Folder);
        }));
    }

    private OleMenuCommand Add(int id, Func<Task> run)
    {
        var command = new OleMenuCommand((_, _) => JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            try
            {
                await run();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ShowError(ex.Message);
            }
        }).FileAndForget("TotalGit/Command"), new CommandID(CommandSet, id));
        _commands!.AddCommand(command);
        return command;
    }

    /// <summary>A command that runs straight away on the UI thread (menus call it there).</summary>
    private OleMenuCommand AddSync(int id, Action run)
    {
        var command = new OleMenuCommand((_, _) =>
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                run();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ShowError(ex.Message);
            }
        }, new CommandID(CommandSet, id));
        _commands!.AddCommand(command);
        return command;
    }

    private WorktreeEntry? SelectedWorktree()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        return (FindToolWindow(typeof(WorktreesWindow), 0, false) as WorktreesWindow)?.Control?.Selected;
    }

    private void OnSelected(Action<WorktreeEntry> run)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (SelectedWorktree() is { } entry) run(entry);
    }

    /// <summary>The Worktrees window's right-click menu, at a screen point.</summary>
    internal void ShowWorktreeMenu(int x, int y)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _commands?.ShowContextMenu(new CommandID(CommandSet, WorktreeMenuId), x, y);
    }

    /// <summary>Double-click or Enter in the Worktrees window.</summary>
    internal void SwitchTo(WorktreeEntry entry) =>
        JoinableTaskFactory.RunAsync(async () =>
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            try
            {
                await OpenWorktreeAsync(entry, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ShowError(ex.Message);
            }
        }).FileAndForget("TotalGit/Switch");

    // ------------------------------------------------------------------ switching

    private async Task SwitchWorktreeAsync()
    {
        await Model.RefreshAsync();
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        if (Model.Entries.Count == 0)
        {
            ShowStatus(Model.Message ?? "No worktrees found.");
            return;
        }
        var items = Model.Entries.Select(e =>
        {
            var row = new WorktreeItem(e);
            return new PickItem(e.Name, row.Summary, e.Folder, e.Current) { Tag = e };
        }).ToList();
        var dialog = new PickDialog("Switch Worktree", $"Worktrees of {Model.RepoName}. Pick one to open here, or in a new Visual Studio.",
            items, items.FirstOrDefault(i => !((WorktreeEntry)i.Tag!).Current), offerNewWindow: true);
        if (dialog.ShowModal() != true || dialog.Picked?.Tag is not WorktreeEntry picked) return;
        await OpenWorktreeAsync(picked, dialog.InNewWindow);
    }

    /// <summary>Opens a worktree's solution here (closing this one) or in a new Visual Studio.</summary>
    private async Task OpenWorktreeAsync(WorktreeEntry entry, bool newWindow)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        if (!entry.Exists)
        {
            ShowError($"The folder {entry.Folder} doesn't exist any more.");
            return;
        }
        if (entry.Current && !newWindow) return;

        var target = await ChooseTargetAsync(entry);
        if (target is null) return;

        if (newWindow)
        {
            StartVisualStudio(target);
            return;
        }

        // Closing asks about unsaved changes; cancelling there stays put.
        if (_solution is not null && _solution.CloseSolutionElement((uint)__VSSLNSAVEOPTIONS.SLNSAVEOPT_PromptSave, null, 0) != VSConstants.S_OK)
            return;
        if (Directory.Exists(target)) _dte?.ExecuteCommand("File.OpenFolder", GitRunner.Quote(target));
        else ErrorHandler.ThrowOnFailure(_solution!.OpenSolutionFile(0, target));
    }

    /// <summary>
    /// What to open in a worktree: the same solution as now (by its place in the worktree), else the one last chosen
    /// there, else the only one, else one picked from a list. A folder when this Visual Studio has a folder open, or
    /// the worktree has no solution. Null when cancelled.
    /// </summary>
    private async Task<string?> ChooseTargetAsync(WorktreeEntry entry)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        if (InFolderMode()) return entry.Folder;

        _solution!.GetSolutionInfo(out _, out var currentFile, out _);
        if (!string.IsNullOrEmpty(currentFile) && Model.CurrentEntry is { } current && Porcelain.IsUnder(currentFile, current.Folder))
        {
            var same = Path.Combine(entry.Folder, currentFile.Substring(current.Folder.TrimEnd('\\').Length + 1));
            if (File.Exists(same)) return same;
        }

        var store = new ShellSettingsManager(this).GetWritableSettingsStore(SettingsScope.UserSettings);
        var key = entry.Folder.ToLowerInvariant().Replace('\\', '/');
        if (store.CollectionExists(LastSolutionsCollection) && store.GetString(LastSolutionsCollection, key, "") is { Length: > 0 } last && File.Exists(last))
            return last;

        var solutions = await Task.Run(() => Porcelain.FindSolutions(entry.Folder));
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        if (solutions.Count == 0) return entry.Folder;
        if (solutions.Count == 1) return solutions[0];

        var items = solutions.Select(s =>
        {
            var dir = Path.GetDirectoryName(s)!;
            var relative = Porcelain.SamePath(dir, entry.Folder) ? "(top folder)" : dir.Substring(entry.Folder.TrimEnd('\\').Length + 1);
            return new PickItem(Path.GetFileName(s), relative, s) { Tag = s };
        }).ToList();
        var dialog = new PickDialog("Open Solution", $"{entry.Name} has {solutions.Count} solutions. Which one?", items, null, offerNewWindow: false);
        if (dialog.ShowModal() != true || dialog.Picked?.Tag is not string picked) return null;
        store.CreateCollection(LastSolutionsCollection);
        store.SetString(LastSolutionsCollection, key, picked);
        return picked;
    }

    /// <summary>Starts another Visual Studio (the same one, with the same settings hive) on a solution or folder.</summary>
    private void StartVisualStudio(string target)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var devenv = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? throw new InvalidOperationException("Can't tell where Visual Studio is.");
        var args = GitRunner.Quote(target);
        // The experimental instance (/rootsuffix Exp) starts others in the same instance.
        if (GetService(typeof(SVsShell)) is IVsShell shell
            && ErrorHandler.Succeeded(shell.GetProperty((int)__VSSPROPID.VSSPROPID_VirtualRegistryRoot, out var root))
            && root is string rootText
            && Regex.Match(rootText, @"\d+\.\d+_[0-9a-f]+([A-Za-z]\w*)$") is { Success: true } suffix)
        {
            args = $"/rootsuffix {suffix.Groups[1].Value} {args}";
        }
        System.Diagnostics.Process.Start(new ProcessStartInfo(devenv, args) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target) ?? "" })?.Dispose();
    }

    // ------------------------------------------------------------------ Total Git

    private string? TotalGitExe()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var configured = (GetDialogPage(typeof(OptionsPage)) as OptionsPage)?.TotalGitPath;
        var exe = ExternalApps.FindTotalGit(configured);
        if (exe is not null) return exe;
        var message = string.IsNullOrWhiteSpace(configured)
            ? "Total Git isn't installed (or isn't where its installer puts it).\n\nOpen its download page?"
            : $"Total Git wasn't found at {configured} (Tools > Options > Total Git).\n\nOpen its download page?";
        if (VsShellUtilities.ShowMessageBox(this, message, "Total Git", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_YESNO,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST) == (int)VSConstants.MessageBoxResult.IDYES)
            System.Diagnostics.Process.Start(new ProcessStartInfo(ExternalApps.ReleasesUrl) { UseShellExecute = true })?.Dispose();
        return null;
    }

    private void OpenInTotalGit(string folder)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (TotalGitExe() is { } exe) ExternalApps.OpenInTotalGit(exe, folder);
    }

    /// <summary>Merges each selected conflicted file in Total Git's merge window, staging it once saved there.</summary>
    private async Task ResolveConflictsAsync()
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
        var files = SelectedFiles().Where(Model.IsConflicted).ToList();
        if (files.Count == 0 || TotalGitExe() is not { } exe) return;
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            ShowStatus($"Merging {name} in Total Git…");
            var (exit, message) = await ExternalApps.MergeAsync(exe, file);
            await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
            if (exit == (int)MergeExit.Cancelled)
            {
                ShowStatus($"{name} is still conflicted.");
                break;
            }
            if (exit != (int)MergeExit.Saved)
            {
                ShowError(message.Length > 0 ? message : $"Total Git couldn't merge {name}.");
                continue;
            }
            try
            {
                await GitRunner.RunAsync(Path.GetDirectoryName(file)!, "add -- " + GitRunner.Quote(file));
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                ShowStatus($"{name} resolved and staged.");
            }
            catch (GitException ex)
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync(DisposalToken);
                ShowError($"{name} was saved, but staging it failed: {ex.Message}");
            }
        }
        Model.RefreshSoon(0);
    }

    /// <summary>The files selected in Solution Explorer (or the document whose tab was right-clicked), else the active document.</summary>
    private List<string> SelectedFiles()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var files = new List<string>();
        if (GetService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection monitor
            && ErrorHandler.Succeeded(monitor.GetCurrentSelection(out var hierPtr, out var itemId, out var multi, out var containerPtr)))
        {
            try
            {
                if (multi is not null)
                {
                    multi.GetSelectionInfo(out var count, out _);
                    var items = new VSITEMSELECTION[count];
                    multi.GetSelectedItems(0, count, items);
                    foreach (var item in items) AddFile(files, item.pHier, item.itemid);
                }
                else if (hierPtr != IntPtr.Zero && Marshal.GetObjectForIUnknown(hierPtr) is IVsHierarchy hierarchy)
                {
                    AddFile(files, hierarchy, itemId);
                }
            }
            finally
            {
                if (hierPtr != IntPtr.Zero) Marshal.Release(hierPtr);
                if (containerPtr != IntPtr.Zero) Marshal.Release(containerPtr);
            }
        }
        if (files.Count == 0 && _dte?.ActiveDocument?.FullName is { Length: > 0 } active) files.Add(active);
        return files;
    }

    private static void AddFile(List<string> files, IVsHierarchy? hierarchy, uint itemId)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (hierarchy is null) return;
        if (ErrorHandler.Succeeded(hierarchy.GetCanonicalName(itemId, out var name)) && File.Exists(name)) files.Add(name);
    }

    // ------------------------------------------------------------------ messages

    private void ShowStatus(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        (GetService(typeof(SVsStatusbar)) as IVsStatusbar)?.SetText(text);
    }

    private void ShowError(string text)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        VsShellUtilities.ShowMessageBox(this, text, "Total Git", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }
}
