# RepoSync

A Windows (WPF, .NET 10) app for keeping many GitHub repositories up to date in one local folder.
List the repos of a profile, tick the ones you want, and pull the latest. It checks out the branch with the
newest commit. It also shows how each repo compares with its remote, draws its commit graph, packs its libraries
into a local NuGet feed, and opens its solution.

## Requirements

- Windows, with the .NET 10 SDK (`net10.0-windows`)
- `git` on the `PATH`. Git Credential Manager (included with Git for Windows) is used for sign-in.

## Build and run

```
dotnet run --project RepoSync.csproj
```

or open `RepoSync.slnx` in Visual Studio.

## Using it

1. Enter your **GitHub profile** (a username or a profile URL) and click **Load repos**.
   Add a **token** to include private repos. With a token the profile box is only used for the git sign-in.
2. Set the **Local folder**. Each repo is cloned to `<folder>\<repo name>`.
3. Tick repos and click **Pull latest & checkout latest branch**, or use a row's **Update** button.
   Repos that aren't cloned yet are cloned first.
4. **Check for updates** fetches every repo and compares it with its remote.

Re-running **Load repos** merges into the list, so existing rows keep their branch, status and dates.

### Columns

| Column | Meaning |
|---|---|
| Branch / Last commit | What is checked out now, and its first commit-message line. Changes after an update are animated. |
| Update | Updates just that repo. |
| Pack | Packs the repo's libraries into the NuGet feed. See below. |
| Open | Opens the repo's solution file. If there are several, **▾** lists them. |
| Last updated | Time today, `Yesterday`, a weekday for the past week, otherwise the date. |
| Status | ✔ fine · ↻ out of sync with the remote · ✘ failed · – not cloned · … busy |
| Message | What happened or what is wrong, such as `Updated`, `No changes`, `Behind 2 · newer branch: dev`, or the error text. |

The panel on the right shows the selected repo's commit graph (latest 300 commits, merges included).

### "Latest branch"

The branch with the most recent commit, by committer date, among the `origin/*` branches. `--ff-only` is used for
the pull, so local changes or diverged branches are reported as errors and never overwritten.

### Pack

Set a **NuGet feed** folder. A repo's **Pack** button is enabled when it contains **library** projects (not
`Exe`/`WinExe`, unless packed as a tool) whose version is ahead of the packages in the feed. The button
shows how many, and its tooltip lists them. A moving bar shows while the repo is scanned or packed. Tick
**Include libraries not yet in feed** to also allow libraries that have never been packed.

Clicking **Pack** runs `dotnet pack -c Release` for each project and moves the `.nupkg` into the feed.

## Settings

Saved to `%APPDATA%\RepoSync\settings.json`: profile, folders, the repo list with each row's state, and the time of
the last save. The token is encrypted with Windows DPAPI for your user account, so it can't be read on another
machine or account.

### Token permissions

The token is only used to list repos. A fine-grained token needs **Metadata: Read-only**, with the right *Resource
owner* (an organisation for org repos) and access to the repos you want. Cloning and pulling use your git login.

### Which account does git use?

The profile name is passed to git as `credential.https://github.com.username`, so Git Credential Manager doesn't
ask which of several signed-in GitHub accounts to use.

## Projects

| Folder | |
|---|---|
| `RepoSync.csproj` | The WPF app: windows and XAML only. `MainWindow*.cs` is split by feature: list and updates, graph, animation, pack, open. |
| `RepoSync.Core/` | No UI. `Models/`: `RepoItem` (one table row) and `Settings`. `Services/`: `GitService` (runs git, sync, remote check), `GitHubClient` (repo listing), `CommitGraph` (builds the BranchKit tree). |
| `NuGetCollector.Core/` | Scans for packages and projects and packs them. Shared by RepoSync and NuGetCollector. |
| `NuGetCollector/` | A standalone app. It moves newer `.nupkg` files from a solutions folder into a feed, and can pack projects that are ahead of the feed. |

The commit graph uses the [BranchKit](https://github.com/roly-289/undokit) NuGet package.

## Versioning

The version is `<Version>` in `RepoSync.csproj`. It is shown in the window title.
