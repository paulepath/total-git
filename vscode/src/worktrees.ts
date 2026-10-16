// Parsing git's porcelain output. No VS Code here, so the tests can run under plain node.
import * as path from 'node:path';

export interface Worktree {
    /** The folder, as git prints it (forward slashes on Windows). */
    path: string;
    head?: string;
    /** The short branch name, or undefined when detached or bare. */
    branch?: string;
    bare: boolean;
    detached: boolean;
    locked: boolean;
    /** git would prune it: its folder is gone. */
    prunable: boolean;
    /** The first one git lists: the repository's main working folder. */
    isMain: boolean;
}

/** Parses `git worktree list --porcelain`. */
export function parseWorktreeList(output: string): Worktree[] {
    const result: Worktree[] = [];
    let current: Worktree | undefined;
    for (const raw of output.split('\n')) {
        const line = raw.replace(/\r$/, '');
        if (line.startsWith('worktree ')) {
            current = {
                path: line.slice('worktree '.length),
                bare: false, detached: false, locked: false, prunable: false,
                isMain: result.length === 0,
            };
            result.push(current);
            continue;
        }
        if (!current) continue;
        const [key, ...rest] = line.split(' ');
        const value = rest.join(' ');
        switch (key) {
            case 'HEAD': current.head = value; break;
            case 'branch': current.branch = value.replace(/^refs\/heads\//, ''); break;
            case 'detached': current.detached = true; break;
            case 'bare': current.bare = true; break;
            case 'locked': current.locked = true; break;
            case 'prunable': current.prunable = true; break;
        }
    }
    return result;
}

export interface WorktreeStatus {
    upstream?: string;
    ahead: number;
    behind: number;
    /** Changed, staged, conflicted and untracked entries. */
    changes: number;
    conflicts: number;
}

/** Parses `git status --porcelain=v2 --branch`. */
export function parseStatus(output: string): WorktreeStatus {
    const status: WorktreeStatus = { ahead: 0, behind: 0, changes: 0, conflicts: 0 };
    for (const raw of output.split('\n')) {
        const line = raw.replace(/\r$/, '');
        if (line === '') continue;
        if (line.startsWith('# branch.upstream ')) {
            status.upstream = line.slice('# branch.upstream '.length);
        } else if (line.startsWith('# branch.ab ')) {
            const m = /\+(\d+) -(\d+)/.exec(line);
            if (m) {
                status.ahead = Number(m[1]);
                status.behind = Number(m[2]);
            }
        } else if (!line.startsWith('#')) {
            status.changes++;
            if (line.startsWith('u ')) status.conflicts++;
        }
    }
    return status;
}

/** The worktree's folder name, as Total Git names it. */
export function worktreeName(worktreePath: string): string {
    return path.basename(worktreePath.replace(/[\\/]+$/, ''));
}

/** Whether two folders are the same (ignoring case and slashes on Windows). */
export function samePath(a: string, b: string, windows = process.platform === 'win32'): boolean {
    const norm = (p: string) => {
        let s = p.replace(/\\/g, '/').replace(/\/+$/, '');
        if (windows) s = s.toLowerCase();
        return s;
    };
    return norm(a) === norm(b);
}

/** "↑2 ↓1" (only the non-zero parts). */
export function aheadBehindText(status: WorktreeStatus | undefined): string {
    if (!status) return '';
    const parts: string[] = [];
    if (status.ahead) parts.push(`↑${status.ahead}`);
    if (status.behind) parts.push(`↓${status.behind}`);
    return parts.join(' ');
}

/** Runs `work` over the items with at most `limit` at once, keeping the order. */
export async function mapLimit<T, R>(items: readonly T[], limit: number, work: (item: T) => Promise<R>): Promise<R[]> {
    const results = new Array<R>(items.length);
    let next = 0;
    const runners = Array.from({ length: Math.min(limit, items.length) }, async () => {
        while (next < items.length) {
            const i = next++;
            results[i] = await work(items[i]);
        }
    });
    await Promise.all(runners);
    return results;
}
