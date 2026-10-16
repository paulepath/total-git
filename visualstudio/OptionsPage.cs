using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace TotalGit.VisualStudio;

/// <summary>Tools > Options > Total Git.</summary>
public sealed class OptionsPage : DialogPage
{
    [Category("Total Git")]
    [DisplayName("Total Git path")]
    [Description(@"Path to TotalGit.exe. Leave empty to use the installed copy (%LOCALAPPDATA%\TotalGitApp\current\TotalGit.exe).")]
    public string TotalGitPath { get; set; } = "";
}
