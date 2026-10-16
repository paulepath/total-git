import assert from 'node:assert/strict';
import { test } from 'node:test';
import { aheadBehindText, mapLimit, parseStatus, parseWorktreeList, samePath, worktreeName } from '../src/worktrees.ts';

test('reads main, linked, detached and prunable worktrees', () => {
    const list = parseWorktreeList([
        'worktree D:/code/total-git',
        'HEAD 4bbf7f2aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        'branch refs/heads/master',
        '',
        'worktree D:/code/total-git/.worktrees/merge tool',
        'HEAD 1111111aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        'branch refs/heads/feature/merge-tool-mode',
        'locked',
        '',
        'worktree D:/code/total-git/.worktrees/look',
        'HEAD 2222222aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        'detached',
        '',
        'worktree D:/code/total-git/.worktrees/gone',
        'HEAD 3333333aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        'branch refs/heads/old',
        'prunable gitdir file points to non-existent location',
        '',
    ].join('\r\n'));

    assert.equal(list.length, 4);
    assert.deepEqual(list.map(w => w.isMain), [true, false, false, false]);
    assert.equal(list[0].branch, 'master');
    assert.equal(list[1].path, 'D:/code/total-git/.worktrees/merge tool');
    assert.equal(list[1].branch, 'feature/merge-tool-mode');
    assert.equal(list[1].locked, true);
    assert.equal(list[2].detached, true);
    assert.equal(list[2].branch, undefined);
    assert.equal(list[2].head, '2222222aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
    assert.equal(list[3].prunable, true);
});

test('reads a bare repository first', () => {
    const list = parseWorktreeList('worktree /srv/repo.git\nbare\n\nworktree /srv/wt\nHEAD abc\nbranch refs/heads/main\n');
    assert.equal(list[0].bare, true);
    assert.equal(list[0].isMain, true);
    assert.equal(list[1].branch, 'main');
});

test('counts changes, conflicts and ahead/behind', () => {
    const status = parseStatus([
        '# branch.oid 4bbf7f2aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
        '# branch.head feature/x',
        '# branch.upstream origin/feature/x',
        '# branch.ab +2 -1',
        '1 .M N... 100644 100644 100644 aaa aaa src/a.ts',
        '2 R. N... 100644 100644 100644 aaa aaa R100 b.ts\tc.ts',
        'u UU N... 100644 100644 100644 100644 aaa bbb ccc d.ts',
        '? new file.txt',
        '',
    ].join('\n'));
    assert.equal(status.upstream, 'origin/feature/x');
    assert.equal(status.ahead, 2);
    assert.equal(status.behind, 1);
    assert.equal(status.changes, 4);
    assert.equal(status.conflicts, 1);
    assert.equal(aheadBehindText(status), '↑2 ↓1');
});

test('a clean branch without upstream', () => {
    const status = parseStatus('# branch.oid abc\n# branch.head main\n');
    assert.deepEqual(status, { ahead: 0, behind: 0, changes: 0, conflicts: 0 });
    assert.equal(aheadBehindText(status), '');
});

test('names and compares folders', () => {
    assert.equal(worktreeName('D:/code/total-git/.worktrees/merge tool/'), 'merge tool');
    assert.equal(worktreeName('D:\\code\\total-git'), 'total-git');
    assert.ok(samePath('D:/Code/Total-Git/', 'd:\\code\\total-git', true));
    assert.ok(!samePath('/a/B', '/a/b', false));
});

test('mapLimit keeps the order and the limit', async () => {
    let running = 0;
    let most = 0;
    const result = await mapLimit([5, 1, 3, 2, 4], 2, async n => {
        running++;
        most = Math.max(most, running);
        await new Promise(r => setTimeout(r, n));
        running--;
        return n * 10;
    });
    assert.deepEqual(result, [50, 10, 30, 20, 40]);
    assert.equal(most, 2);
});
