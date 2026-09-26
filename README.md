# actions-common

Every ReactiveUI repository builds, tests, signs and releases its code the same way. This repository holds the
shared pieces that do that work. Each repository calls them instead of keeping its own copy.

## Table of Contents

- [What this repository gives you](#what-this-repository-gives-you)
- [Calling a workflow](#calling-a-workflow)
- [Every repository uses `@main`](#every-repository-uses-main)
- [Reusable workflows](#reusable-workflows)
- [Composite actions](#composite-actions)
- [Steps are written in C#](#steps-are-written-in-c)
- [How a version is chosen](#how-a-version-is-chosen)
- [The signer image](#the-signer-image)
- [The CodeQL pack](#the-codeql-pack)
- [The release train](#the-release-train)
- [How dependencies stay current](#how-dependencies-stay-current)
- [Why there is no dependency cache](#why-there-is-no-dependency-cache)
- [Coverage from three operating systems](#coverage-from-three-operating-systems)
- [WinUI tests](#winui-tests)

## What this repository gives you

- **Reusable workflows.** A [reusable workflow](https://docs.github.com/en/actions/sharing-automations/reusing-workflows)
  is a whole GitHub Actions workflow that another repository runs with `uses:`.
- **Composite actions.** A [composite action](https://docs.github.com/en/actions/sharing-automations/creating-actions/creating-a-composite-action)
  is a group of steps that a workflow runs as one step.
- **A signer image.** This container image signs NuGet packages with the organisation's code-signing certificate.
- **A CodeQL pack.** This pack tells CodeQL to trust actions published by this organisation.

## Calling a workflow

Call a reusable workflow from a job in your own workflow:

```yaml
jobs:
  build:
    permissions:
      contents: read
      actions: write
    uses: reactiveui/actions-common/.github/workflows/workflow-common-setup-and-build.yml@main
    with:
      installWorkloads: true
    secrets:
      CODECOV_TOKEN: ${{ secrets.CODECOV_TOKEN }}
```

Grant the permissions listed at the top of the workflow file. Each input has a `description` in the file.

ReactiveUI's [`ci-build.yml`](https://github.com/reactiveui/ReactiveUI/blob/main/.github/workflows/ci-build.yml)
is a working example.

## Every repository uses `@main`

Repositories call these workflows at `@main`. A change merged here reaches every repository on its next run.

This repository has no CI that runs the reusable workflows. The repositories that call them are the test.

## Reusable workflows

| Workflow | What it does |
|---|---|
| `workflow-common-setup-and-build.yml` | Builds and tests on Windows, Linux and macOS, then uploads coverage to Codecov. |
| `workflow-common-release.yml` | Chooses the release version, builds and packs, then signs the packages in the signer image. |
| `workflow-common-release-unsigned.yml` | Builds and packs without signing. |
| `workflow-common-create-release.yml` | Creates the GitHub release and its tag, with release notes and the packages attached. |
| `workflow-common-publish-github-packages.yml` | Pushes packages to GitHub Packages. |
| `workflow-common-sonarcloud.yml` | Runs SonarCloud analysis on pushes and on pull requests from the same repository. |
| `workflow-common-codeql.yml` | Runs CodeQL on C#, on the repository's GitHub Actions and, if you ask, on JavaScript. |
| `workflow-common-aot-smoke.yml` | Publishes a native AOT test app on Windows, Linux and macOS and runs it. |
| `workflow-common-benchmarks.yml` | Runs [BenchmarkDotNet](https://github.com/dotnet/BenchmarkDotNet) projects and writes the results to the run summary. |
| `workflow-common-benchmarks-ab.yml` | Benchmarks two commits on one runner and marks each benchmark faster, slower or unresolved. |

The build, SonarCloud, CodeQL and AOT workflows skip work a change cannot affect. A push or pull request that only
changes `.github` skips the build, tests, SonarCloud and the C# and JavaScript analysis. CodeQL analyses GitHub
Actions only when something under `.github` changed. Skipped jobs still pass required checks.

## Composite actions

| Action | What it does |
|---|---|
| `dotnet-environment` | Installs the .NET 8 to 11 SDKs, adds Windows Defender exclusions, restores workloads and sets the version. Set `stamp-version: 'false'` in a job that has no checkout. |
| `dotnet-build` | Restores and builds with the .NET CLI. |
| `dotnet-build-uno` | Restores, builds and packs an Uno solution with MSBuild. |
| `dotnet-test` | Runs the tests on [Microsoft Testing Platform](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-intro) and uploads coverage and diagnostic logs. `test-projects` (`testProjects` on the build and SonarCloud workflows) takes globs that narrow the run to matching projects, with `!` lines to exclude. |
| `minver` | Reads the version from git tags and exports it for the build. |
| `compute-version-and-tag` | Works out the next release version from the latest release tag. |
| `sonarcloud` | Starts a SonarCloud scan before the build and finishes it after the tests. |
| `certum-sign` | Signs `.nupkg` files inside the signer image. |
| `dotnet-benchmarks` | Checks out a commit and runs benchmark projects for the A/B workflow. |
| `detect-changes` | Reports whether a push or pull request changes files under `.github`, files outside it, or both. |

## Steps are written in C#

When a step needs more than one command, it runs a C# script. The .NET SDK reads the script from standard input
with `dotnet run -`:

```yaml
- name: Resolve release tag
  shell: bash
  env:
    VERSION_OVERRIDE: ${{ inputs.versionOverride }}
    TAG_PREFIX: ${{ inputs.minverTagPrefix }}
  run: |
    cd "$RUNNER_TEMP" && dotnet run - -- "$VERSION_OVERRIDE" "$TAG_PREFIX" <<'CS'
    if (args is not [var versionOverride, var tagPrefix])
    {
        return 2;
    }

    // ...
    CS
```

Every script follows these rules:

- **Inputs arrive through `env:`.** The step passes them to the script as arguments. The script text never
  contains `${{ }}`, so a value from a pull request cannot change the script's code.
- **The script runs from `$RUNNER_TEMP`.** `dotnet run` reads `global.json` and `Directory.Build.props` from the
  folder it runs in. The temp folder keeps the calling repository's settings out of the script.
- **The script needs the .NET 11 SDK.** A job runs `dotnet-environment` before its first script.
- **A script declares what it needs at the top.** For example, `#:package System.Management@*` adds a package.

## How a version is chosen

- **Every build gets a version from git tags.** [MinVer](https://github.com/adamralph/minver) reads the latest tag. A build after that tag gets a
  pre-release version.
- **A release picks its version first.** `bump` adds one to the major, minor or patch number of the latest
  release. `preRelease` adds an `alpha`, `beta` or `rc` label with a counter. `versionOverride` sets an exact
  version, for example for a backport.
- **The tag comes last.** `workflow-common-create-release.yml` creates the tag at the commit that was built. A
  build or signing failure leaves no tag behind.

## The signer image

`docker/certum-signer/Dockerfile` builds `ghcr.io/reactiveui/certum-signer`. The release workflow signs packages
inside it.

**Why it exists.** Certum SimplySign has no command-line signing. Its key is only reachable through the SimplySign
Desktop app. The image runs that app on a virtual display and logs in with `xdotool`. It then signs with
[jsign](https://github.com/ebourg/jsign) and checks the result with `dotnet nuget verify`.

**What it is built on.** The image starts from the .NET 11 SDK on Ubuntu 26.04, pinned by digest. It adds the
.NET 10 SDK.

**Why it builds libxml2.** SimplySign ships its own copy of Qt 5.9. That Qt needs `libxml2.so.2`. Ubuntu 26.04
only ships `libxml2.so.16`, the name
[libxml2 2.14 moved to](https://discourse.gnome.org/t/libxml2-2-14-0-released/28025). So the image builds libxml2 2.13.9, the last release with `libxml2.so.2`. It also
builds libxslt 1.1.43, the last release that works with that libxml2.

**How it catches a missing library.** The build runs `ldd` on SimplySign and its display plugin. The build fails
if either one is missing a library.

**One installer quirk.** The SimplySign installer stops every process whose command line contains
`SimplySignDesktop`. The install step names the install folder only through `$SS_DIST` for that reason.

**Who can pull it.** The image is private. A workflow that calls the release workflow grants `packages: read`.

**When it rebuilds.** The image rebuilds when its Dockerfile changes on `main`, every Monday, and when you start
the workflow by hand. The weekly rebuild picks up Ubuntu security fixes, .NET 10 patches and new SimplySign
releases.

## The CodeQL pack

`codeql/actions-trusted-owners` is published as `reactiveui/actions-trusted-owners`.

CodeQL's [`actions/unpinned-tag`](https://codeql.github.com/codeql-query-help/actions/actions-unpinned-tag/) query
flags any action that a workflow references by a tag or branch, such as `@main`. It trusts only GitHub's own
publishers. Every ReactiveUI repository calls this repository at `@main` on purpose. Without the pack, CodeQL would
flag every shared workflow in every repository.

The pack adds `reactiveui` to the owners CodeQL trusts. Actions from any other owner are still flagged.
`workflow-common-codeql.yml` loads the pack when it analyses GitHub Actions.

- **Its version counts commits.** The patch number is the number of commits that changed the pack. Every change
  publishes a new version on its own.
- **Its settings live in `qlpack.yml`.** The CodeQL CLI ignores `extensionTargets` in `codeql-pack.yml`. GitHub's
  [model pack guide](https://docs.github.com/en/code-security/codeql-cli/using-the-advanced-functionality-of-the-codeql-cli/creating-and-working-with-codeql-packs)
  describes the format.
- **It is public.** Each repository downloads it with its own `GITHUB_TOKEN`, which cannot read private packages.

## The release train

The release train releases several ReactiveUI repositories in dependency order. Other repositories do not call it.
You start it from this repository's Actions tab with `release-train.yml`.

For example, a Splat release needs ReactiveUI, Akavache and the others to move to the new Splat version and release
too. The train does that work for you:

1. It releases Splat with Splat's own `release.yml`.
2. It waits until NuGet lists every new Splat package.
3. It opens a pull request in each repository that depends on Splat. The pull request updates the package
   versions.
4. It merges the pull request when its checks pass, then releases that repository.
5. It repeats steps 2 to 4 down the dependency graph.

A repository whose latest release already points at the head of its branch has nothing new to ship. The train
reuses that release instead of releasing again.

Only the packages a library ships with decide the order. A repository that uses another repository's packages only
in its tests does not list it in `dependsOn`. Its test pins still move to that repository's latest release.

### The config

`build/release-train.json` lists the repositories. Each entry names a repository and the repositories it
`dependsOn`. The train releases a repository only after everything it depends on has released.

```json
{
  "name": "ReactiveUI",
  "dependsOn": ["Primitives", "splat", "ReactiveUI.Binding.SourceGenerators"],
  "releaseInputs": { "bump": "{bump}", "channel": "{channel}" }
}
```

| Setting | What it does |
|---|---|
| `name` | The repository name. The repository is `<owner>/<name>` unless you set `repository`. |
| `dependsOn` | Repositories that must release first. |
| `releaseInputs` | Inputs for the repository's release workflow. `{bump}` and `{channel}` take the train's values. Leave out `{channel}` when the workflow has no channel input. |
| `releaseWorkflow`, `branch` | The release workflow file and the branch it runs on. The defaults are `release.yml` and `main`. |
| `ignorePackages` | Package IDs the train never updates in this repository. |
| `adminMerge` | Merges the pull request past branch protection. Use it when a ruleset requires a review and lists the App as a bypass actor. |

`groups` names sets of repositories, such as `core`. `timeouts` sets how many minutes the train waits for pull
request checks, the release run and NuGet.

### Starting a train

| Input | What it does |
|---|---|
| `targets` | Repositories or groups to release, separated by commas. `all` releases every repository. |
| `includeDownstream` | Also releases every repository that depends on the targets. |
| `exclude` | Repositories or groups to leave out. |
| `bump`, `channel` | The release level and channel for every repository. |
| `onFailure` | `stop` starts no new level after a failure. `continue` keeps releasing the repositories that do not depend on the failed one. |
| `dryRun` | Shows the plan without releasing anything. |

To release Splat and everything that uses it, set `targets` to `splat` and keep `includeDownstream` on.

A repository with its own preset can call the train as a reusable workflow with fixed inputs.

### How versions are updated

The train reads every `.props`, `.targets` and project file in the repository. It updates a
`PackageVersion` or `PackageReference` that names a package from another repository in the config. When the
version is an MSBuild property such as `$(SplatVersion)`, it updates the property instead.

Each package gets its version from this run's release when the train released it. Otherwise it gets the version
from the repository's latest GitHub release. The train never lowers a version, and it leaves ranges and other
expressions alone.

### When a repository fails

The train stops at the failure and reports it. The run summary lists every repository with its result, version,
pull request, release and each version it changed. It ends with the inputs to resume.

Say ReactiveUI fails to build with the new Splat version:

1. Fix the train's pull request in ReactiveUI and merge it.
2. Start the train again with `targets` set to `ReactiveUI`.

Splat does not release again. ReactiveUI picks up the Splat version that already shipped from Splat's latest
release. The train reuses an open pull request on its `release-train/dependencies` branch, so your fix stays.

### The GitHub App

The train acts as an organisation GitHub App. The built-in `GITHUB_TOKEN` cannot do this work: pull requests it
opens do not start CI, and it cannot start workflows in other repositories.

An App token expires after one hour, and a train waits longer than that. So the scripts sign their own tokens
with the App's private key and replace each token before it expires. Commits and pull requests appear as
`<app-name>[bot]`.

The train reads two organisation secrets, and this repository must have access to both:

| Secret | Value |
|---|---|
| `RELEASE_TRAIN_APP_CLIENT_ID` | The App's client ID. |
| `RELEASE_TRAIN_APP_PRIVATE_KEY` | The whole `.pem` private key file. |

Install the App on every repository in the config. The plan job stops before it releases anything when the App is
missing from a planned repository. Give the App these repository permissions:

| Permission | Why |
|---|---|
| Contents: read and write | Push the update branch and merge the pull request. |
| Pull requests: read and write | Open, comment on and merge the pull request. |
| Actions: read and write | Start the release workflow and follow its run. |
| Checks: read, Commit statuses: read | Read the pull request's check results. |

When a ruleset requires a review, add the App to the ruleset's bypass list and set `adminMerge` for that
repository.

## How dependencies stay current

Renovate opens pull requests for dependency updates. It uses the organisation's
[preset](https://github.com/reactiveui/.github/blob/main/renovate.json), which every ReactiveUI repository shares,
and the rules in `.github/renovate.json`.

- **Docker images and actions from `actions/*`, `github/*` and `microsoft/*` are pinned by digest.** A digest names
  one exact image or commit. Renovate updates the digest on the day a new version appears.
- **Trusted updates merge on their own.** These are minor, patch and digest updates to those actions, and digest
  updates to `mcr.microsoft.com`, `ghcr.io/reactiveui` and `docker/dockerfile` images.
- **`reactiveui/*` actions stay on `@main`.** Every repository picks up a shared change on its next run.
- **The Dockerfile's jsign version is tracked** through its `# renovate:` comment. The SimplySign download has no
  source Renovate can read, so you update it by hand.

## Why there is no dependency cache

GitHub keeps a separate cache for each branch, and each repository gets 10 GB. Pull requests filled that space and
pushed out the entries builds needed. On Windows, saving the NuGet cache also took longer than restoring the
packages from scratch. So no workflow here caches NuGet packages or workloads.

## Coverage from three operating systems

Each operating system uploads its coverage as a temporary artifact. The collect job merges the three into one
artifact with [`actions/upload-artifact/merge`](https://github.com/actions/upload-artifact/blob/main/merge/README.md)
and deletes the temporary ones. It then uploads the merged coverage
to Codecov.

## WinUI tests

A WinUI test needs the Windows App Runtime. Workloads do not install it and the runner image does not have it.
Set `installWindowsAppRuntime: true` and the workflow installs it before the tests run.

The runtime defaults to the latest stable `Microsoft.WindowsAppSDK` major.minor, which Renovate keeps current.
Set `windowsAppRuntimeVersion` only to pin an older runtime.

## Sponsors

[JetBrains](https://www.jetbrains.com/) gives ReactiveUI's maintainers licences for its tools through its
[open source support programme](https://www.jetbrains.com/community/opensource/).
[Anthropic](https://www.anthropic.com/) supports them with [Claude](https://claude.com/) through
[Claude for Open Source](https://claude.com/contact-sales/claude-for-oss).
[OpenAI](https://openai.com/) supports them with [Codex](https://openai.com/codex/) through
[Codex for Open Source](https://developers.openai.com/community/codex-for-oss).

[![JetBrains](https://raw.githubusercontent.com/reactiveui/website/main/docs/images/sponsors/jetbrains.svg)](https://www.jetbrains.com/)
[![Claude by Anthropic](https://raw.githubusercontent.com/reactiveui/website/main/docs/images/sponsors/claude.svg)](https://claude.com/)
[![OpenAI](https://raw.githubusercontent.com/reactiveui/website/main/docs/images/sponsors/openai.svg)](https://openai.com/codex/)

See [our sponsors](https://www.reactiveui.net/sponsors/) for more information.
JetBrains, Claude, Anthropic, OpenAI and Codex names and logos are trademarks of their respective owners.
