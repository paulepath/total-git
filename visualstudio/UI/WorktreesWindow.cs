using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace TotalGit.VisualStudio.UI;

/// <summary>View > Other Windows > Worktrees (Total Git).</summary>
[Guid("368dcce8-2258-4469-a535-0efdd91112c6")]
public sealed class WorktreesWindow : ToolWindowPane
{
    public WorktreesWindow() : base(null)
    {
        Caption = "Worktrees (Total Git)";
        ToolBar = new CommandID(TotalGitPackage.CommandSet, TotalGitPackage.ToolbarId);
    }

    internal WorktreesControl? Control => Content as WorktreesControl;

    protected override void OnCreate()
    {
        base.OnCreate();
        Content = new WorktreesControl((TotalGitPackage)Package);
    }
}
