# Standardized MinVer bump-and-release across repos

**Date:** 2026-05-23
**Status:** Approved (design)
**Repo:** reactiveui/actions-common (shared infrastructure), consumed by ~10 downstream repos (Extensions, NuSourceDocs, …)

## Problem

`actions-common/.github/workflows/workflow-common-release.yml` runs MinVer against
whatever commit `HEAD` points at. MinVer is tag-first: unless `HEAD` sits exactly on a
`vX.Y.Z` tag, it deliberately emits a **pre-release** (`2.4.0-alpha.0.<height>`), never a
clean RTM `2.4.0`. There is no way to cut a clean major/minor/patch release through the
shared workflow.

`NuSourceDocs/.github/workflows/release.yml` already solves this with a self-contained
"compute next version + create tag" step driven by a `workflow_dispatch` `bump` input. We
want that capability standardized in `actions-common` so all ~10 repos share one
consistent release pipeline, while keeping each repo's `release.yml` thin.

### Hard constraint — NuGet trusted publishing (OIDC)

The `dotnet nuget push` and its `NuGet/login` OIDC step **must live directly in each
repo's `release.yml`**. This is non-negotiable and locked down by OIDC: trusted publishing
binds the minted token to the repository and its workflow file.

- A **reusable workflow** (`jobs.x.uses: actions-common/...workflow.yml`) changes the OIDC
  `job_workflow_ref` claim to point at actions-common → **breaks** trusted publishing.
- A **composite action** (`steps: - uses: actions-common/.github/actions/...`) runs inside
  the caller's job, so the token still carries the caller repo + caller workflow → would be
  OIDC-safe — but per the constraint above we still keep the login + push as plain in-repo
  steps, not abstracted at all.

**Boundary rule:** everything except the OIDC login + `nuget push` may be centralized; the
push stays per-repo as hand-written steps.

## Goal / success criteria

1. A dispatched release with `bump = major|minor|patch` produces a clean RTM version
   (`X.Y.Z`), tags the repo `vX.Y.Z`, signs with Certum, and publishes.
2. The same `workflow-common-release.yml` still serves CI (no bump → pre-release) unchanged.
3. Each repo's `release.yml` is a thin, near-identical template; behavior changes are made
   once in `actions-common`.
4. NuGet OIDC login + push remain in each repo's `release.yml`.
5. The bump arithmetic / tag-selection logic is unit-tested.

## Architecture

```
caller release.yml (every repo, thin + identical)
  on: workflow_dispatch { bump: major|minor|patch }
  permissions: { contents: write, id-token: write }
  jobs:
    release:        uses actions-common/workflow-common-release.yml@main
                      with:  { bump, solutionFile, versioningTool: minver, ... }
                      secrets: { CERTUM_USER_ID, CERTUM_OTP_URI, CERTUM_CERT_FINGERPRINT }
                      outputs: semver2, tag
    publish-nuget:  needs: release            # IN-REPO, OIDC — never centralized
                      download signed-nuget artifact
                      NuGet/login (OIDC, user = secrets.NUGET_USER)
                      dotnet nuget push
    create-release: needs: [release, publish-nuget]
                      uses actions-common/workflow-common-create-release.yml@main
                      with: { version: needs.release.outputs.semver2,
                              tag:     needs.release.outputs.tag }
```

### Component 1 — `compute-version-and-tag` composite action

Path: `actions-common/.github/actions/compute-version-and-tag/action.yml`

Inputs:
- `bump` — `major` | `minor` | `patch` | `''` (empty). Required-by-convention only when a
  release is intended; empty is a valid no-op.
- `tag-prefix` — default `v`.

Behavior:
- Empty/unset `bump` → **no-op**. Emits empty outputs; CI path is unaffected (MinVer at HEAD
  produces the usual pre-release).
- Non-empty `bump`:
  1. List RTM tags matching `<prefix>[0-9]*.[0-9]*.[0-9]*`, excluding any containing `-`
     (pre-release), `sort -V`, take the last → latest RTM `X.Y.Z` (strip prefix).
  2. Fail with a clear error if no RTM tag exists (first release must be tagged manually, or
     a follow-up enhancement adds a `seed-version` input — out of scope here).
  3. Increment: major → `(X+1).0.0`; minor → `X.(Y+1).0`; patch → `X.Y.(Z+1)`.
  4. `git config user.name/email` to the github-actions bot identity.
  5. `git tag <prefix><version>` and `git push origin <prefix><version>`.
  6. Export `MINVERVERSIONOVERRIDE=<version>` to `GITHUB_ENV`.

Outputs:
- `version` — e.g. `2.4.0`
- `tag` — e.g. `v2.4.0`

Notes:
- Tagging HEAD before the MinVer step means MinVer naturally computes the clean `X.Y.Z`
  (height 0). Exporting `MINVERVERSIONOVERRIDE` is belt-and-suspenders and also skips
  per-project git walks during pack.
- Validation of `bump` value (reject anything other than the four accepted values) happens
  in the action.

### Component 2 — `workflow-common-release.yml` change

- Add input `bump` (`type: string`, `default: ''`,
  description: "major|minor|patch to cut a clean RTM release and tag it; empty = CI
  pre-release build").
- Add a step after **Checkout** (already `fetch-depth: 0`, which fetches tags) and before
  **Setup .NET Environment**:
  ```yaml
  - name: Compute version and tag (release only)
    if: inputs.bump != ''
    id: bump
    uses: reactiveui/actions-common/.github/actions/compute-version-and-tag@main
    with:
      bump: ${{ inputs.bump }}
      tag-prefix: ${{ inputs.minverTagPrefix }}
  ```
- Add a `tag` job output alongside `semver2`:
  `tag: ${{ steps.bump.outputs.tag }}` (empty on CI builds).
- `semver2` output is unchanged in mechanism — after tagging, the existing MinVer step
  yields the clean version; for CI it yields the pre-release as before.
- Requires `contents: write` (already declared) so the pushed tag works; the caller grants it
  via its job permissions (callers already set `permissions: contents: write`).

### Component 3 — `workflow-common-create-release.yml` change

- Add optional input `tag` (`type: string`, `default: ''`).
- The "Create GitHub Release" step uses `tag` when provided, otherwise falls back to
  `version` (back-compat). This makes the release attach to the existing `vX.Y.Z` tag rather
  than creating a duplicate unprefixed `X.Y.Z` tag.

### Component 4 — caller `release.yml` template

Reference implementation (Extensions), to be mirrored across the ~10 repos:

```yaml
name: Release
on:
  workflow_dispatch:
    inputs:
      bump:
        description: 'Release level'
        required: true
        type: choice
        options: [patch, minor, major]

permissions:
  contents: write
  id-token: write

jobs:
  release:
    uses: reactiveui/actions-common/.github/workflows/workflow-common-release.yml@main
    with:
      solutionFile: ReactiveUI.Extensions.slnx
      installWorkloads: true
      versioningTool: minver
      minverMinimumMajorMinor: '2.3'
      bump: ${{ inputs.bump }}
    secrets:
      CERTUM_USER_ID: ${{ secrets.CERTUM_USER_ID }}
      CERTUM_OTP_URI: ${{ secrets.CERTUM_OTP_URI }}
      CERTUM_CERT_FINGERPRINT: ${{ secrets.CERTUM_CERT_FINGERPRINT }}

  publish-nuget:               # IN-REPO, OIDC — NEVER centralized
    needs: release
    runs-on: ubuntu-latest
    environment: { name: release }
    permissions: { id-token: write }
    steps:
      - uses: actions/download-artifact@v8
        with: { name: signed-nuget }
      - uses: actions/setup-dotnet@v5
      - id: nuget-login
        uses: NuGet/login@v1
        with: { user: ${{ secrets.NUGET_USER }} }
      - shell: bash
        run: |
          for pkg in *.nupkg; do
            dotnet nuget push "$pkg" --source https://api.nuget.org/v3/index.json \
              --api-key "${{ steps.nuget-login.outputs.NUGET_API_KEY }}"
          done

  create-release:
    needs: [release, publish-nuget]
    uses: reactiveui/actions-common/.github/workflows/workflow-common-create-release.yml@main
    with:
      version: ${{ needs.release.outputs.semver2 }}
      tag: ${{ needs.release.outputs.tag }}
```

Per-repo differences are confined to `with:` inputs (`solutionFile`,
`minverMinimumMajorMinor`, `installWorkloads`, etc.) — the structure is identical.

## Data flow (release run)

1. Maintainer dispatches `release.yml` with `bump = minor`.
2. `release` job (Windows): checkout (full history + tags) → `compute-version-and-tag`
   computes `2.4.0`, pushes tag `v2.4.0`, sets `MINVERVERSIONOVERRIDE=2.4.0` → MinVer step
   stamps `2.4.0` → build → pack → Certum sign → upload `signed-nuget`. Outputs
   `semver2=2.4.0`, `tag=v2.4.0`.
3. `publish-nuget` job (in-repo): download artifact → OIDC login → push to NuGet.
4. `create-release` job: download artifact → generate notes → `gh release create v2.4.0`
   (title `2.4.0`), attaching the `.nupkg` files.

CI run (no dispatch / `bump` empty): step 2's bump action is skipped; everything else behaves
as today, producing a pre-release build with no tag and no publish.

## Error handling

- `bump` not one of `major|minor|patch` (when non-empty) → action fails fast.
- No reachable RTM `vX.Y.Z` tag → action fails with an explanatory message (manual first tag
  required; seeding is a possible future input, out of scope).
- Tag push lacking `contents: write` → fails at push; documented requirement that callers
  grant `contents: write`.
- A tag that already exists (re-dispatch of an already-released version) → `git tag`/`git
  push` fails; surfaced as a hard error rather than silently re-releasing.

## Testing

- **Unit (shell logic):** extract and exercise the bump/tag-selection logic against a fixture
  set of tags — latest-RTM detection ignoring pre-release tags and bare (non-prefixed) tags,
  correct major/minor/patch arithmetic, prefix handling, and the no-RTM-tag error path. Same
  approach used to validate the Certum TOTP code.
- **YAML:** lint all changed workflows and the new action.
- **OIDC regression:** confirm the caller `publish-nuget` job (login + push) is untouched and
  remains in-repo.
- **Manual smoke:** one real dispatched `patch` release on a low-risk repo to confirm the
  clean version, tag, signed packages, NuGet push, and GitHub release.

## Out of scope

- Seeding the very first RTM tag automatically (repos are expected to have an existing
  `vX.Y.Z`).
- Migrating NuSourceDocs's signing from SSL.com to Certum (separate task; NuSourceDocs can
  adopt this pipeline as part of that).
- Changing the NuGet trusted-publishing identity model.

## Rollout

1. Land Components 1–3 in `actions-common`.
2. Convert Extensions `release.yml` to the template (Component 4) as the first consumer.
3. Roll the template out to the remaining repos.
