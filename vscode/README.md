# Total Git for VS Code

See a repository's worktrees and switch between them without leaving VS Code, and hand things over to
[Total Git](https://totalgit.e-path.co.uk).

## Worktrees

The **Total Git** icon in the activity bar opens a list of the repository's worktrees, found by asking git, so
worktrees anywhere (including Total Git's `.worktrees/<name>`) show up. Each row shows:

- the branch, or the commit it's detached at;
- how far ahead and behind its upstream it is (↑2 ↓1);
- how many uncommitted changes it has (●3).

The worktree this window has open is highlighted.

- **Click** a worktree to switch this window to it.
- **The window button** on a row opens it in a new window.
- **Right-click** a row to open it in Total Git, reveal it in File Explorer, open a terminal there or copy its path.

The status bar shows the current worktree; click it (or run **Total Git: Switch Worktree…**) for a quick list of
the others. Each one in that list has a button to open it in a new window instead.

## Total Git

- **Open in Total Git** shows the repository or worktree in Total Git, in the copy that's already running.
- **Resolve Conflict in Total Git** is on conflicted files in Source Control's *Merge Changes* group and on an
  editor tab's right-click menu. It opens Total Git's merge window on the file and stages it once you save there.

Total Git is found at `%LOCALAPPDATA%\TotalGitApp\current\TotalGit.exe`. If it's somewhere else, set
`totalGit.path`.

## Settings

| Setting | |
|---|---|
| `totalGit.path` | Path to `TotalGit.exe`. Leave empty to use the installed copy. |
| `totalGit.showStatusBar` | Show the current worktree in the status bar. |
