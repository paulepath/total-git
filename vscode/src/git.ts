import { execFile } from 'node:child_process';

/** The git executable: VS Code's git extension's when it found one, else whatever is on PATH. */
let gitPath = 'git';

export function setGitPath(p: string | undefined): void {
    if (p) gitPath = p;
}

export class GitError extends Error {
    constructor(message: string, readonly stderr: string) {
        super(message);
    }
}

/** Runs git in a folder and returns its output. Never goes through a shell. */
export function git(cwd: string, args: string[], timeoutMs = 15000): Promise<string> {
    return new Promise((resolve, reject) => {
        execFile(gitPath, args, { cwd, timeout: timeoutMs, windowsHide: true, maxBuffer: 32 * 1024 * 1024, encoding: 'utf8' },
            (error, stdout, stderr) => {
                if (error) reject(new GitError(stderr.trim() || error.message, stderr));
                else resolve(stdout);
            });
    });
}
