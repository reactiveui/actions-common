# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Zero Tolerance Policy

- **NEVER abandon work halfway through** - if something gets difficult, push through it
- **NEVER use `git stash`** to hide incomplete work - fix the problem directly
- **NEVER give up because a task is complex** - break it down and keep going
- If a tool call is rejected, adapt your approach immediately and continue

## What This Repository Is

This repository holds the GitHub Actions every ReactiveUI repository shares: reusable workflows, composite
actions, the Certum signer container image and a CodeQL model pack. README.md describes each one.

**CRITICAL:** Every repository calls this one at `@main`. A change pushed to `main` reaches every repository on
its next run.

## Verifying a Change

**CRITICAL:** There is no local way to run a reusable workflow. Push the change and let CI decide. Read the
failing log before proposing a cause.

- `actionlint .github/workflows/*.yml` lints the workflows. It does not read the composite actions in
  `.github/actions/`.
- To compile an inline script, copy the text between `<<'CS'` and `CS` into a `.cs` file **outside the
  repository** and run `dotnet build file.cs`.
- `publish-certum-signer.yml` and `publish-codeql-model-pack.yml` run on a push to `main` that changes their
  files. Watch them with `gh run list` and read failures with `gh run view <id> --log-failed`.
- Let `publish-certum-signer.yml` build the signer image. Do not build it locally.
- The other workflows only run from a calling repository. Check a change there.

## Inline C# Scripts

Any step with logic beyond a single command runs C#. Single commands such as `dotnet restore`, `gh codeql pack
publish` or `apt-get install` stay as plain `run:` lines. Do not write logic in bash, PowerShell or Python.

### The Shape Every Script Follows

```yaml
- name: Stamp pack version
  shell: bash
  env:
    PACK_DIR: ${{ inputs.packDir }}
  run: |
    cd "$RUNNER_TEMP" && dotnet run - -- "$PACK_DIR" <<'CS'
    using System.Diagnostics;
    using static System.Environment;

    if (args is not [var packDir])
    {
        Console.WriteLine("::error::Expected the pack directory argument.");
        return 2;
    }

    Directory.SetCurrentDirectory(GetEnvironmentVariable("GITHUB_WORKSPACE")!);

    // ...

    File.AppendAllLines(GetEnvironmentVariable("GITHUB_OUTPUT")!, [$"version={version}"]);
    return 0;
    CS
```

- **Inputs go through `env:`, then arguments.** Never put `${{ }}` inside the script text. A value from a pull
  request or a fork's branch name would become code.
- **Secrets stay in the environment.** Read them with `GetEnvironmentVariable`. Arguments show up in the process
  list.
- **Run from `$RUNNER_TEMP`.** `dotnet run` reads `global.json`, `Directory.Build.props` and analyzers from the
  current folder and its parents. Change to `GITHUB_WORKSPACE` inside the script when it needs the checkout.
- **Check the argument count with a list pattern.** Report a mismatch with `::error::` and return 2.
- **Return the exit code** of anything that fails. Report errors as `::error::` lines.
- **Write outputs and environment** with `File.AppendAllLines` to `GITHUB_OUTPUT` and `GITHUB_ENV`.
- **Set up .NET 11 first.** A job runs `dotnet-environment` before its first script. Pass `stamp-version: 'false'`
  when the job has no checkout or stamps the version later.

### .NET 11 Process APIs

Scripts drive processes with the
[.NET 11 process APIs](https://devblogs.microsoft.com/dotnet/process-api-improvements-in-dotnet-11/). This is how they
behave on the .NET 11 SDK:

| API | Behaviour |
|---|---|
| `Process.Run(fileName, arguments, silent, timeout)` | Returns `ProcessExitStatus` with `ExitCode`, `Canceled` and `Signal`. The child shares the console. |
| `Process.Run(..., silent: true)` | Discards the child's output. |
| `Process.Run(..., timeout: ...)` | Kills the child when the time runs out: `Canceled` is `true`, `ExitCode` is 137 and `Signal` is `SIGKILL`. It does not throw. |
| `Process.RunAndCaptureText(fileName, arguments)` | Returns `ProcessTextOutput` with `StandardOutput`, `StandardError`, `ExitStatus` and `ProcessId`. |
| `Process.RunAndCaptureText(ProcessStartInfo)` | Throws `InvalidOperationException` unless `RedirectStandardOutput` and `RedirectStandardError` are both `true`. |
| `Process.StartAndForget(ProcessStartInfo)` | With `StartDetached = true`, the child keeps running after the script exits. Give it `File.OpenHandle(...)` for `StandardOutputHandle` and `StandardErrorHandle` to keep its log. |
| `File.OpenNullHandle()` | Returns a handle that discards whatever a child writes to it. |

**A bare file name resolves in the host's folder before `PATH`.** A script that `dotnet` started, and that runs
`dotnet`, gets that same `dotnet`. A fake `dotnet` placed first on `PATH` only takes effect when the script runs
through its own apphost.

### `dotnet run -` and File-Based Apps

- **Arguments follow `--`.** An empty string arrives as an empty argument.
- **Directives go at the top of the script:** `#:package Name@Version` (`@*` takes the latest), `#:property
  Key=Value`, `#:sdk`, `#:project` and `#:include`. A property name cannot contain `:`.
- **Native AOT is on by default.** Add `#:property PublishAot=false` when a package relies on reflection.
- **Windows-only APIs need a Windows target framework:** `#:property TargetFramework=net11.0-windows10.0.19041.0`.
- **The SDK caches each build** by source text, directives, SDK version and nearby build files. Two copies of the
  same script running at once compete for that cache.
- **The .NET 11 SDK starts an MSBuild server by default.** Set `DOTNET_CLI_USE_MSBUILD_SERVER=0` where a lingering
  server would get in the way, as the benchmarks workflow does.
- Microsoft's reference: [File-based apps](https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps)

### C# Style in Scripts

Use the newest C# the .NET 11 SDK accepts:

- **List patterns** for arguments: `if (args is not [var bump, var tagPrefix])`.
- **Property patterns** for results: `if (Process.Run("pip", ["install", "codecov-cli"]) is { ExitCode: not 0 } install)`.
- **Constant patterns** for strings: `if (preReleaseId is "")`, `pushTag is "true"`.
- **Collection expressions** with spreads, and `with(...)` to pass a capacity or comparer:
  `Dictionary<string, int> geometry = [with(StringComparer.Ordinal)];`.
- **Extension blocks** for small helpers:
  ```csharp
  internal static class ProcessOutputExtensions
  {
      extension(ProcessTextOutput output)
      {
          public string[] Lines => output.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
      }
  }
  ```
- **`params IEnumerable<string>`** for a helper that forwards arguments to a process.
- **Allman braces**, four-space indents, explicit `StringComparison` and `CultureInfo.InvariantCulture`.
- **`using static System.Environment;`** for `GetEnvironmentVariable`.

### Windows Runners

- `shell: bash` on Windows is Git Bash. It rewrites arguments that look like paths, so `/d:sonar.x=y` loses its
  slash. A step that passes such arguments sets `MSYS2_ARG_CONV_EXCL: '*'`.

## The Signer Image

- **Base images are pinned by digest.** Renovate updates both `FROM` lines together. Do not put the reference back
  into an `ARG`.
- **Stay on libxml2 2.13.x and libxslt 1.1.43.** libxml2 2.14
  [renames the library](https://discourse.gnome.org/t/libxml2-2-14-0-released/28025) to `libxml2.so.16`, which
  SimplySign's bundled WebKit cannot load. libxslt 1.1.44 and later require libxml2 2.15.
- **Never write `SimplySignDesktop` in the `RUN` step that runs the installer.** The installer stops every process
  whose command line contains that name, including the build step. Use `$SS_DIST`.
- **Do not add `libqt5*` packages.** SimplySign bundles its own Qt 5.9.
- **Keep the library check.** It runs `ldd` on SimplySign and its X platform plugin, and it is the only proof that
  the image can start SimplySign.
- **Ubuntu 26.04 is the base on purpose.** .NET 11 images ship for Ubuntu 26.04, Azure Linux and Alpine only, per
  dotnet-docker's [platform policy](https://github.com/dotnet/dotnet-docker/blob/main/documentation/supported-platforms.md). Azure
  Linux 3.0 has no `xdotool`, window manager or Java runtime. Alpine uses musl, and SimplySign is a glibc build.

## The CodeQL Pack

- **Do not bump the patch version by hand.** `publish-codeql-model-pack.yml` sets it to the number of commits that
  changed the pack. Change `major.minor` in `qlpack.yml` when the pack needs a new line.
- **Keep the metadata in `qlpack.yml`.** The CLI ignores `extensionTargets` in `codeql-pack.yml`. See GitHub's
  [model pack guide](https://docs.github.com/en/code-security/codeql-cli/using-the-advanced-functionality-of-the-codeql-cli/creating-and-working-with-codeql-packs).
- **Keep the package public.** Calling repositories read it with their own `GITHUB_TOKEN`.

## Renovate

- `.github/renovate.json` extends the organisation's
  [preset](https://github.com/reactiveui/.github/blob/main/renovate.json). Rules here apply after the organisation's.
- The preset pins Docker images and `actions/*`, `github/*` and `microsoft/*` actions by digest with no release
  delay. It merges minor, patch and digest updates to those actions, and digest updates to `mcr.microsoft.com`,
  `ghcr.io/reactiveui` and `docker/dockerfile` images, automatically. Change those rules in the preset, not here.
- `reactiveui/*` actions stay on `@main`. Do not pin them.
- A Dockerfile `ARG` with a `# renovate: datasource=... depName=...` comment on the line above is tracked.

## Skipping Jobs by Changed Files

- The build, SonarCloud, CodeQL and AOT workflows start with a `changes` job that runs `detect-changes`.
- A push or pull request that only changes `.github` skips the code jobs. CodeQL skips the Actions analysis when
  nothing under `.github` changed.
- **Skip with `if:`, never with `paths:` filters.** Repositories require these checks. A skipped job passes a
  required check, and a workflow that never starts leaves the check waiting.
- **A matrix job skips its steps, not the job.** A skipped matrix job reports one check without the matrix values,
  so `build-unix (ubuntu-latest)` would never report.
- **Guard dependents with `!cancelled()`.** The `changes` job skips on events other than push and pull request,
  and a skipped dependency skips every job after it unless the condition says otherwise.

## Decisions Already Made

- **No NuGet or workload caching.** Per-branch caches filled the 10 GB repository budget and evicted each other.
  On Windows, saving the package cache took longer than a cold restore.
- **Workload restore stays driven by the solution.** An explicit workload list goes stale whenever a repository
  adds a target framework.
- **The CodeQL publish job runs on the plain runner**, not inside a container.
- **Coverage artifacts are merged with
  [`actions/upload-artifact/merge`](https://github.com/actions/upload-artifact/blob/main/merge/README.md).** Do not search the repository's artifact
  list; a large repository holds over 100,000 artifacts.

## Conventions

### Comments

- A YAML or Dockerfile comment explains something that is not obvious from the code.
- Do not describe what a step used to do, or which approaches were tried.
- A step's `name:` and an input's `description:` already explain it. Do not repeat them in a comment.

### Commits

- Use Conventional Commits: `ci(scope): subject`, `fix(scope): subject`, `build(scope): subject`.
- Give each change one bullet in the body.

## Writing Docs

These rules cover README.md, CLAUDE.md and every other doc in the repository.

### Who you write for

Write for a reader at a grade 8 level who knows GitHub Actions basics. They know what a workflow, a job and a
step are. They do not know this repository.

### Sentences

- Put the main point first.
- Give each sentence one subject. Use two only when they are tightly coupled.
- Keep sentences short. Split a sentence that needs a dash, a semicolon or a "which" to hold together.
- Use the active voice. Say who does what: "the workflow signs the packages", not "the packages are signed".
- Use verbs, not nouns made from verbs. Write "decide", not "make a decision".
- Say what is true. Avoid double negatives.
- Cut words that add nothing. Do not restate a point in the next sentence.

### Words

- Use everyday words. When you need a technical term, define it the first time you use it.
- Define each term once. After that, use it without explaining it again.
- Use the same word for the same thing every time. Do not swap in a synonym for variety.
- Use "you" for the reader.

### Structure

- Use headings so a reader can find a topic.
- Use a list for steps or for separate items. Use a table to compare items across the same columns.
- Show a short code example when it explains faster than words.

### Scope

- Describe the repository as it is. Do not describe what it used to do.
