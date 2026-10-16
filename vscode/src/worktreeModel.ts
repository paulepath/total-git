import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';
import { git } from './git';
import { mapLimit, parseStatus, parseWorktreeList, samePath, worktreeName, type Worktree, type WorktreeStatus } from './worktrees';

export interface RepoInfo {
    /** The shared .git folder, which identifies the repository whichever worktree it was found from. */
    commonDir: string;
    /** The main worktree's folder (the first git lists). */
    mainRoot: string;
    name: string;
    worktrees: WorktreeEntry[];
}

export interface WorktreeEntry {
    repo: RepoInfo;
    worktree: Worktree;
    /** The folder as a native path. */
    folder: string;
    name: string;
    status?: WorktreeStatus;
    exists: boolean;
    /** This window has it open. */
    current: boolean;
}

/** The worktrees of every repository in the window's folders, kept fresh. */
export class WorktreeModel implements vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChange = this.changed.event;

    repos: RepoInfo[] = [];
    /** True once the first refresh has finished. */
    loaded = false;

    private timer: NodeJS.Timeout | undefined;
    private running: Promise<void> | undefined;
    private again = false;
    private watchers = new Map<string, vscode.FileSystemWatcher>();
    private extraFolders: () => string[] = () => [];

    /** Folders besides the workspace folders to look for repositories in (the git extension's repositories). */
    setExtraFolders(get: () => string[]): void {
        this.extraFolders = get;
    }

    /** Refreshes after a short pause, so a burst of events makes one refresh. */
    refreshSoon(delayMs = 400): void {
        if (this.timer) clearTimeout(this.timer);
        this.timer = setTimeout(() => {
            this.timer = undefined;
            void this.refresh();
        }, delayMs);
    }

    async refresh(): Promise<void> {
        if (this.running) {
            this.again = true;
            return this.running;
        }
        this.running = (async () => {
            do {
                this.again = false;
                try {
                    this.repos = await this.read();
                } catch {
                    // Keep what was shown; the next refresh tries again.
                }
                this.loaded = true;
                this.watch();
                this.changed.fire();
            } while (this.again);
        })();
        try {
            await this.running;
        } finally {
            this.running = undefined;
        }
    }

    /** The worktree this window has open (the first, when several folders are open). */
    get currentEntry(): WorktreeEntry | undefined {
        for (const repo of this.repos) {
            const found = repo.worktrees.find(w => w.current);
            if (found) return found;
        }
        return undefined;
    }

    get allEntries(): WorktreeEntry[] {
        return this.repos.flatMap(r => r.worktrees);
    }

    private async read(): Promise<RepoInfo[]> {
        const workspaceFolders = (vscode.workspace.workspaceFolders ?? []).filter(f => f.uri.scheme === 'file').map(f => f.uri.fsPath);
        const candidates = [...new Set([...workspaceFolders, ...this.extraFolders()])];

        // Each folder's worktree root and shared git folder.
        const found = await mapLimit(candidates, 4, async folder => {
            try {
                const out = await git(folder, ['rev-parse', '--path-format=absolute', '--show-toplevel', '--git-common-dir']);
                const [top, common] = out.split(/\r?\n/);
                return top && common ? { folder, top: path.normalize(top), common: path.normalize(common) } : undefined;
            } catch {
                return undefined;
            }
        });
        const openRoots = found.filter(f => f && workspaceFolders.includes(f.folder)).map(f => f!.top);

        const byCommon = new Map<string, string>();
        for (const f of found) {
            if (f && ![...byCommon.keys()].some(k => samePath(k, f.common))) byCommon.set(f.common, f.top);
        }

        const repos: RepoInfo[] = [];
        for (const [commonDir, anyRoot] of byCommon) {
            let list: Worktree[];
            try {
                list = parseWorktreeList(await git(anyRoot, ['worktree', 'list', '--porcelain']));
            } catch {
                continue;
            }
            const mainRoot = path.normalize(list[0]?.path ?? anyRoot);
            const repo: RepoInfo = { commonDir, mainRoot, name: worktreeName(mainRoot), worktrees: [] };
            repo.worktrees = list.filter(w => !w.bare).map(w => {
                const folder = path.normalize(w.path);
                return {
                    repo, worktree: w, folder,
                    name: worktreeName(folder),
                    exists: !w.prunable && fs.existsSync(folder),
                    current: openRoots.some(r => samePath(r, folder)),
                };
            });
            await mapLimit(repo.worktrees.filter(e => e.exists), 4, async e => {
                try {
                    // --no-optional-locks: reading status mustn't rewrite the index (and set off the watchers).
                    e.status = parseStatus(await git(e.folder, ['--no-optional-locks', 'status', '--porcelain=v2', '--branch'], 10000));
                } catch {
                    e.status = undefined;
                }
            });
            repos.push(repo);
        }
        return repos;
    }

    /** Watches each repository's branches and worktree list (in its git folder, outside the workspace). */
    private watch(): void {
        const wanted = new Set(this.repos.map(r => r.commonDir));
        for (const [dir, watcher] of this.watchers) {
            if (!wanted.has(dir)) {
                watcher.dispose();
                this.watchers.delete(dir);
            }
        }
        for (const dir of wanted) {
            if (this.watchers.has(dir)) continue;
            const watcher = vscode.workspace.createFileSystemWatcher(
                new vscode.RelativePattern(vscode.Uri.file(dir), '{HEAD,packed-refs,refs/heads/**,worktrees/*,worktrees/*/HEAD}'));
            watcher.onDidCreate(() => this.refreshSoon());
            watcher.onDidChange(() => this.refreshSoon());
            watcher.onDidDelete(() => this.refreshSoon());
            this.watchers.set(dir, watcher);
        }
    }

    dispose(): void {
        if (this.timer) clearTimeout(this.timer);
        for (const w of this.watchers.values()) w.dispose();
        this.watchers.clear();
        this.changed.dispose();
    }
}
