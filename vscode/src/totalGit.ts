import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as path from 'node:path';
import * as vscode from 'vscode';

const releasesUrl = 'https://github.com/paulepath/total-git/releases/latest';

/** How `TotalGit.exe --merge` ended (its exit code). */
export const enum MergeExit {
    Saved = 0,
    Cancelled = 1,
    Usage = 2,
    Unsupported = 3,
}

/** The Total Git executable: the totalGit.path setting, else where its installer puts it. */
export function findTotalGit(): string | undefined {
    const configured = vscode.workspace.getConfiguration('totalGit').get<string>('path')?.trim();
    if (configured) return fs.existsSync(configured) ? configured : undefined;
    const local = process.env.LOCALAPPDATA;
    if (!local) return undefined;
    // current\TotalGit.exe, not the "Total Git.exe" launcher beside it: only the app itself passes back an exit code.
    const installed = path.join(local, 'TotalGitApp', 'current', 'TotalGit.exe');
    return fs.existsSync(installed) ? installed : undefined;
}

/** The executable, or undefined after telling the user it wasn't found. */
async function requireTotalGit(): Promise<string | undefined> {
    const exe = findTotalGit();
    if (exe) return exe;
    const configured = vscode.workspace.getConfiguration('totalGit').get<string>('path')?.trim();
    const choice = await vscode.window.showErrorMessage(
        configured ? `Total Git wasn't found at ${configured}.` : "Total Git isn't installed (or isn't where its installer puts it).",
        'Download Total Git', 'Set its path');
    if (choice === 'Download Total Git') await vscode.env.openExternal(vscode.Uri.parse(releasesUrl));
    if (choice === 'Set its path') await vscode.commands.executeCommand('workbench.action.openSettings', 'totalGit.path');
    return undefined;
}

/** Opens a folder in Total Git (in the running one, which shows it in a tab). */
export async function openInTotalGit(folder: string): Promise<void> {
    const exe = await requireTotalGit();
    if (!exe) return;
    const child = spawn(exe, [folder], { detached: true, stdio: 'ignore', cwd: folder });
    child.on('error', err => void vscode.window.showErrorMessage(`Couldn't start Total Git: ${err.message}`));
    child.unref();
}

/** Opens Total Git's merge window on a conflicted file and waits for it to close. */
export async function mergeInTotalGit(file: string): Promise<{ exit: number; message: string } | undefined> {
    const exe = await requireTotalGit();
    if (!exe) return undefined;
    return new Promise(resolve => {
        const child = spawn(exe, ['--merge', file], { cwd: path.dirname(file), stdio: ['ignore', 'ignore', 'pipe'] });
        let stderr = '';
        child.stderr.setEncoding('utf8');
        child.stderr.on('data', (chunk: string) => (stderr += chunk));
        child.on('error', err => resolve({ exit: MergeExit.Usage, message: err.message }));
        child.on('close', code => resolve({ exit: code ?? MergeExit.Cancelled, message: stderr.trim() }));
    });
}
