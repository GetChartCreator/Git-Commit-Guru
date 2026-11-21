# Git Commit Guru

A portable Windows app that turns recent Git commits into a browsable file tree. Switch between local projects and drag files or folders from a commit into another Windows app.

## Download for Windows

**[Download Git Commit Guru (.exe)](https://github.com/GetChartCreator/Git-Commit-Guru/releases/latest/download/Git.Commit.Guru.exe)**

Download the executable and open it. Git for Windows is required. You can also browse the [latest release](https://github.com/GetChartCreator/Git-Commit-Guru/releases/latest) or build from the source using the instructions below.

## Why use it?

Suppose your project is already running on a server and a coding agent has changed a few files across a large local repository. If you deploy manually, finding those files and recreating the right folder structure on the server can take longer than the transfer itself.

Git Commit Guru shows exactly which files changed in a local commit, in the same folder structure as your project. Select the commit, find the files at a glance, and drag them into a transfer client such as MobaXterm. You can move one file, a folder of changed files, or a selection across several folders.

This workflow works with local Git commits. It fits projects you deploy without GitHub or a remote Git repository. Your transfer client still handles the server connection and upload.

## Quick start

1. Open the downloaded `Git.Commit.Guru.exe`. Git for Windows must be installed.
2. Select **Add project…** and choose a local Git repository. The sidebar remembers the projects you add.
3. Select a commit to see its timestamp and changed paths. Double-click a folder to expand or collapse it.
4. Drag a file or folder straight from the tree into Explorer or a client that accepts Windows file drops. Use **Ctrl**, **Shift**, or a selection box to select several items.

Dragging uses the committed version of each file. A folder drop includes the changed files shown below that folder. Deleted paths remain visible for reference. **Open in project** shows the corresponding file or folder in your working copy.

## Make it yours

- Switch between English, Ukrainian, and Russian in the title bar.
- Toggle the light or dark theme and collapse the Projects sidebar when you want more space.
- Move the app folder wherever you like and create a desktop shortcut to the executable. Project paths and appearance preferences are stored locally beside the executable in `projects.txt` and `settings.txt`.

## Build from source

From this folder in Windows PowerShell, use the .NET Framework C# compiler:

```powershell
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $compiler /nologo /target:winexe /codepage:65001 `
  '/out:Git Commit Guru.exe' /win32icon:GitCommitGuru.ico /reference:System.Windows.Forms.dll `
  /reference:System.Drawing.dll .\GitCommitGuru.cs
```

Git Commit Guru reads local repositories and prepares committed files for manual transfer. Your transfer client handles its own server connections and uploads.
