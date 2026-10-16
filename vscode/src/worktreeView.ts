import * as vscode from 'vscode';
import type { RepoInfo, WorktreeEntry, WorktreeModel } from './worktreeModel';
import { aheadBehindText } from './worktrees';

export type Node = { kind: 'repo'; repo: RepoInfo } | { kind: 'worktree'; entry: WorktreeEntry };

/** The Worktrees view: one row per worktree, grouped by repository when the window has several. */
export class WorktreeTree implements vscode.TreeDataProvider<Node> {
    private readonly changed = new vscode.EventEmitter<Node | undefined>();
    readonly onDidChangeTreeData = this.changed.event;

    constructor(private readonly model: WorktreeModel) {
        model.onDidChange(() => this.changed.fire(undefined));
    }

    getChildren(element?: Node): Node[] {
        if (element?.kind === 'repo') return element.repo.worktrees.map(entry => ({ kind: 'worktree', entry }));
        if (element) return [];
        const repos = this.model.repos;
        if (repos.length === 1) return repos[0].worktrees.map(entry => ({ kind: 'worktree', entry }));
        return repos.map(repo => ({ kind: 'repo', repo }));
    }

    getTreeItem(node: Node): vscode.TreeItem {
        if (node.kind === 'repo') {
            const item = new vscode.TreeItem(node.repo.name, vscode.TreeItemCollapsibleState.Expanded);
            item.iconPath = new vscode.ThemeIcon('repo');
            item.description = node.repo.worktrees.length === 1 ? '1 worktree' : `${node.repo.worktrees.length} worktrees`;
            item.tooltip = node.repo.mainRoot;
            item.contextValue = 'repo';
            return item;
        }
        return worktreeItem(node.entry);
    }
}

/** What a worktree has checked out: its branch, or the commit it's detached at. */
export function branchText(entry: WorktreeEntry): string {
    const w = entry.worktree;
    if (w.branch) return w.branch;
    if (w.head) return `detached at ${w.head.slice(0, 7)}`;
    return '';
}

/** "feature/x  ↑2 ↓1  ●3": branch, ahead/behind and the number of uncommitted changes. */
export function summaryText(entry: WorktreeEntry): string {
    if (!entry.exists) return 'folder missing';
    const parts = [branchText(entry), aheadBehindText(entry.status)];
    if (entry.status?.changes) parts.push(`●${entry.status.changes}`);
    if (entry.worktree.isMain) parts.push('· main worktree');
    return parts.filter(p => p).join('  ');
}

function worktreeItem(entry: WorktreeEntry): vscode.TreeItem {
    const label: vscode.TreeItemLabel = entry.current
        ? { label: entry.name, highlights: [[0, entry.name.length]] }
        : { label: entry.name };
    const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
    item.id = `${entry.repo.commonDir}|${entry.folder}`;
    item.description = summaryText(entry);
    item.iconPath = icon(entry);
    item.tooltip = tooltip(entry);
    item.contextValue = ['worktree', entry.current ? 'current' : 'other', entry.exists ? 'openable' : 'missing'].join(' ');
    if (!entry.current && entry.exists) {
        item.command = { command: 'totalgit.switchWorktree', title: 'Switch to Worktree', arguments: [{ kind: 'worktree', entry }] };
    }
    return item;
}

function icon(entry: WorktreeEntry): vscode.ThemeIcon {
    if (!entry.exists) return new vscode.ThemeIcon('warning', new vscode.ThemeColor('list.warningForeground'));
    if (entry.status?.conflicts) return new vscode.ThemeIcon('git-merge', new vscode.ThemeColor('gitDecoration.conflictingResourceForeground'));
    const colour = entry.status?.changes ? new vscode.ThemeColor('gitDecoration.modifiedResourceForeground') : undefined;
    return new vscode.ThemeIcon(entry.current ? 'folder-opened' : 'folder', colour);
}

function tooltip(entry: WorktreeEntry): vscode.MarkdownString {
    const md = new vscode.MarkdownString(undefined, true);
    md.appendMarkdown(`**${escape(entry.name)}**${entry.current ? ' (this window)' : ''}\n\n`);
    md.appendMarkdown(`${escape(entry.folder)}\n\n`);
    const branch = branchText(entry);
    if (branch) md.appendMarkdown(`$(git-branch) ${escape(branch)}${entry.status?.upstream ? ` → ${escape(entry.status.upstream)}` : ''}\n\n`);
    const s = entry.status;
    if (s) {
        if (s.ahead || s.behind) md.appendMarkdown(`${s.ahead} to push, ${s.behind} to pull\n\n`);
        md.appendMarkdown(s.changes ? `${s.changes} uncommitted change${s.changes === 1 ? '' : 's'}` : 'No uncommitted changes');
        if (s.conflicts) md.appendMarkdown(`, ${s.conflicts} conflicted`);
    } else if (!entry.exists) {
        md.appendMarkdown('The folder is gone; Total Git (or `git worktree prune`) can tidy it up.');
    }
    if (entry.worktree.locked) md.appendMarkdown('\n\n$(lock) Locked');
    return md;
}

function escape(text: string): string {
    return text.replace(/[\\`*_{}[\]()#+\-.!|<>]/g, m => `\\${m}`);
}
