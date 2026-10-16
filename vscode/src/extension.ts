import * as path from 'node:path';
import * as vscode from 'vscode';
import { git, setGitPath } from './git';
import { MergeExit, mergeInTotalGit, openInTotalGit } from './totalGit';
import { WorktreeModel, type WorktreeEntry } from './worktreeModel';
import { branchText, summaryText, WorktreeTree, type Node } from './worktreeView';

// The bits of VS Code's git extension API used here.
interface GitApi {
    git: { path: string };
    repositories: { rootUri: vscode.Uri; state: { onDidChange: vscode.Event<void> } }[];
    onDidOpenRepository: vscode.Event<{ rootUri: vscode.Uri; state: { onDidChange: vscode.Event<void> } }>;
    onDidCloseRepository: vscode.Event<unknown>;
}

export function activate(context: vscode.ExtensionContext): void {
    const model = new WorktreeModel();
    const tree = new WorktreeTree(model);
    const view = vscode.window.createTreeView('totalgit.worktrees', { treeDataProvider: tree, showCollapseAll: false });
    const statusItem = vscode.window.createStatusBarItem('totalgit.worktree', vscode.StatusBarAlignment.Left, 49);
    statusItem.name = 'Total Git: Worktree';
    statusItem.command = 'totalgit.switchWorktree';
    context.subscriptions.push(model, view, statusItem);

    const updateStatusItem = () => {
        const current = model.currentEntry;
        const show = vscode.workspace.getConfiguration('totalGit').get<boolean>('showStatusBar', true);
        if (!current || !show) {
            statusItem.hide();
            return;
        }
        const others = current.repo.worktrees.length - 1;
        statusItem.text = `$(list-tree) ${current.name}`;
        statusItem.tooltip = others > 0
            ? `Worktree ${current.name} (${branchText(current)}). Click to switch to one of ${others} other${others === 1 ? '' : 's'}.`
            : `Worktree ${current.name} (${branchText(current)}): the only one.`;
        statusItem.show();
    };
    model.onDidChange(() => {
        updateStatusItem();
        view.message = model.loaded && model.repos.length === 0 ? 'No git repository is open in this window.' : undefined;
    });

    context.subscriptions.push(
        vscode.commands.registerCommand('totalgit.refresh', () => model.refresh()),
        vscode.commands.registerCommand('totalgit.switchWorktree', async (node?: Node) => {
            const entry = node ? entryOf(node) : await pickWorktree(model, 'Switch this window to a worktree');
            if (entry) await openWorktree(entry, false);
        }),
        vscode.commands.registerCommand('totalgit.openWorktreeInNewWindow', async (node?: Node) => {
            const entry = node ? entryOf(node) : await pickWorktree(model, 'Open a worktree in a new window');
            if (entry) await openWorktree(entry, true);
        }),
        vscode.commands.registerCommand('totalgit.openInTotalGit', async (node?: Node) => {
            const folder = node?.kind === 'repo' ? node.repo.mainRoot : node ? entryOf(node)?.folder : await currentFolder(model);
            if (folder) await openInTotalGit(folder);
            else void vscode.window.showInformationMessage('Open a folder in a git repository first.');
        }),
        vscode.commands.registerCommand('totalgit.revealWorktree', (node?: Node) => {
            const entry = node && entryOf(node);
            if (entry) return vscode.commands.executeCommand('revealFileInOS', vscode.Uri.file(entry.folder));
        }),
        vscode.commands.registerCommand('totalgit.openWorktreeInTerminal', (node?: Node) => {
            const entry = node && entryOf(node);
            if (entry) vscode.window.createTerminal({ name: entry.name, cwd: entry.folder }).show();
        }),
        vscode.commands.registerCommand('totalgit.copyWorktreePath', async (node?: Node) => {
            const entry = node && entryOf(node);
            if (entry) await vscode.env.clipboard.writeText(entry.folder);
        }),
        vscode.commands.registerCommand('totalgit.resolveInTotalGit', async (...args: unknown[]) => {
            const files = filesFrom(args);
            if (files.length === 0) {
                void vscode.window.showInformationMessage('Pick a conflicted file (in Source Control or the editor) to resolve.');
                return;
            }
            for (const file of files) {
                if (!(await resolveFile(file))) break;
            }
            model.refreshSoon();
        }),
        vscode.workspace.onDidChangeWorkspaceFolders(() => model.refreshSoon(0)),
        vscode.window.onDidChangeWindowState(s => {
            if (s.focused) model.refreshSoon(0);
        }),
        vscode.workspace.onDidChangeConfiguration(e => {
            if (e.affectsConfiguration('totalGit.showStatusBar')) updateStatusItem();
        }),
    );

    void hookGitExtension(context, model).finally(() => model.refresh());
}

export function deactivate(): void { }

function entryOf(node: Node): WorktreeEntry | undefined {
    return node.kind === 'worktree' ? node.entry : undefined;
}

/** Uses the built-in git extension's git, its repositories (e.g. ones in sub-folders) and its change events. */
async function hookGitExtension(context: vscode.ExtensionContext, model: WorktreeModel): Promise<void> {
    try {
        const extension = vscode.extensions.getExtension<{ getAPI(version: 1): GitApi }>('vscode.git');
        if (!extension) return;
        const api = (extension.isActive ? extension.exports : await extension.activate()).getAPI(1);
        setGitPath(api.git.path);
        model.setExtraFolders(() => api.repositories.map(r => r.rootUri.fsPath));
        const listen = (repo: { state: { onDidChange: vscode.Event<void> } }) =>
            context.subscriptions.push(repo.state.onDidChange(() => model.refreshSoon(1500)));
        api.repositories.forEach(listen);
        context.subscriptions.push(
            api.onDidOpenRepository(repo => {
                listen(repo);
                model.refreshSoon();
            }),
            api.onDidCloseRepository(() => model.refreshSoon()));
    } catch {
        // The git extension is disabled: plain git on PATH and the workspace folders will do.
    }
}

async function openWorktree(entry: WorktreeEntry, newWindow: boolean): Promise<void> {
    if (!entry.exists) {
        void vscode.window.showWarningMessage(`The folder ${entry.folder} doesn't exist any more.`);
        return;
    }
    if (entry.current && !newWindow) return;
    await vscode.commands.executeCommand('vscode.openFolder', vscode.Uri.file(entry.folder), { forceNewWindow: newWindow });
}

type PickItem = vscode.QuickPickItem & { entry: WorktreeEntry };

/** A list of every worktree; Enter picks one, and each row's button opens it in a new window instead. */
async function pickWorktree(model: WorktreeModel, placeholder: string): Promise<WorktreeEntry | undefined> {
    if (!model.loaded) await model.refresh();
    const entries = model.allEntries;
    if (entries.length === 0) {
        void vscode.window.showInformationMessage('No git repository is open in this window.');
        return undefined;
    }
    const newWindowButton: vscode.QuickInputButton = { iconPath: new vscode.ThemeIcon('empty-window'), tooltip: 'Open in new window' };
    const several = model.repos.length > 1;
    const pick = vscode.window.createQuickPick<PickItem>();
    pick.placeholder = placeholder;
    pick.matchOnDescription = true;
    pick.matchOnDetail = true;
    pick.items = entries.map(entry => ({
        entry,
        label: `$(${entry.current ? 'check' : entry.exists ? 'folder' : 'warning'}) ${entry.name}`,
        description: (several ? `${entry.repo.name}  ` : '') + summaryText(entry),
        detail: entry.folder,
        buttons: entry.exists ? [newWindowButton] : [],
    }));
    const current = pick.items.find(i => i.entry.current);
    if (current) pick.activeItems = [pick.items.find(i => !i.entry.current) ?? current];

    return new Promise(resolve => {
        let done = false;
        const finish = (entry: WorktreeEntry | undefined) => {
            if (done) return;
            done = true;
            resolve(entry);
            pick.hide();
        };
        pick.onDidAccept(() => finish(pick.selectedItems[0]?.entry));
        pick.onDidTriggerItemButton(e => {
            finish(undefined);
            void openWorktree(e.item.entry, true);
        });
        pick.onDidHide(() => {
            finish(undefined);
            pick.dispose();
        });
        pick.show();
    });
}

/** This window's worktree, or the folder of the file being edited. */
async function currentFolder(model: WorktreeModel): Promise<string | undefined> {
    if (!model.loaded) await model.refresh();
    const current = model.currentEntry?.folder;
    if (current) return current;
    const doc = vscode.window.activeTextEditor?.document;
    if (doc?.uri.scheme === 'file') {
        try {
            return (await git(path.dirname(doc.uri.fsPath), ['rev-parse', '--show-toplevel'])).trim();
        } catch {
            return undefined;
        }
    }
    return vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
}

/** The files a command was run on: Source Control rows, explorer or editor-tab URIs, else the active editor. */
function filesFrom(args: unknown[]): string[] {
    const files: string[] = [];
    const add = (value: unknown) => {
        if (value instanceof vscode.Uri) {
            if (value.scheme === 'file') files.push(value.fsPath);
        } else if (value && typeof value === 'object' && 'resourceUri' in value && value.resourceUri instanceof vscode.Uri) {
            if (value.resourceUri.scheme === 'file') files.push(value.resourceUri.fsPath);
        } else if (Array.isArray(value)) {
            value.forEach(add);
        }
    };
    args.forEach(add);
    if (files.length === 0) {
        const doc = vscode.window.activeTextEditor?.document;
        if (doc?.uri.scheme === 'file') files.push(doc.uri.fsPath);
    }
    return [...new Set(files)];
}

/** Merges one file in Total Git and stages it once saved, as git mergetool would. False stops a run of several. */
async function resolveFile(file: string): Promise<boolean> {
    const name = path.basename(file);
    const result = await vscode.window.withProgress(
        { location: vscode.ProgressLocation.Window, title: `Merging ${name} in Total Git…` },
        () => mergeInTotalGit(file));
    if (!result) return false;

    switch (result.exit) {
        case MergeExit.Saved:
            try {
                await git(path.dirname(file), ['add', '--', file]);
                void vscode.window.setStatusBarMessage(`$(check) ${name} resolved and staged`, 4000);
            } catch (err) {
                void vscode.window.showErrorMessage(`${name} was saved, but staging it failed: ${(err as Error).message}`);
            }
            return true;
        case MergeExit.Cancelled:
            return false;
        default:
            void vscode.window.showWarningMessage(result.message || `Total Git couldn't merge ${name}.`);
            return true;
    }
}
