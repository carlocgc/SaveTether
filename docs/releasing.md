# Branches and releases

## Branches

- `development` is where work lands. Each change is a feature branch cut from `development`, merged back by pull request.
- `main` holds released code only. It moves forward by a pull request from `development` when a release is ready, and every release is a tag on `main`.
- CI (`.github/workflows/ci.yml`) runs on every pull request and on pushes to `development` and `main`: build, tests, the native binary for win-x64, linux-x64 and linux-arm64, its self-test and the two-device scenario, and the desktop app's publish and the release package, checked file by file. The package is kept as the run's artifact for 14 days.

## Version numbers

`vMAJOR.MINOR.PATCH`, starting at `v0.1.0`. While the major version is 0, a minor bump may change file formats or command behaviour; a patch is fixes only.

The release workflow builds with the version from the tag, so a binary always reports the version it was released as. `Directory.Build.props` holds the version that local and CI builds report; set it to the version being released in the release pull request, so the two agree.

## Making a release

1. On a branch from `development`, set `<Version>` in `Directory.Build.props` and update anything in the docs that names the version. Merge it into `development`.
2. Open a pull request from `development` into `main`, titled with the version (for example `v0.1.0`). Merge it once CI passes.
3. Tag the merge commit on `main` and push the tag:

   ```bash
   git checkout main && git pull
   git tag -a v0.1.0 -m "SaveTether v0.1.0"
   git push origin v0.1.0
   ```

4. The release workflow (`.github/workflows/release.yml`) checks the tag is on `main`, builds and tests all three targets with that version, checks each binary reports it, and publishes a GitHub release with:
   - `savetether-v0.1.0-win-x64.zip`
   - `savetether-v0.1.0-linux-x64.tar.gz`
   - `savetether-v0.1.0-linux-arm64.tar.gz`
   - `SHA256SUMS.txt`

   Each package is one folder named after it, holding:
   - the CLI, `savetether` (`savetether.exe` on Windows);
   - the desktop app, `savetether-gui`, with the native libraries it loads from beside itself: `libSkiaSharp` and `libHarfBuzzSharp` (`.so` on Linux, `.dll` on Windows), and on Windows `av_libglesv2.dll` (ANGLE);
   - `README.md` and `LICENSE`.

   `build/package.sh` builds a package from the two publish folders, leaving out debug symbols. `build/check-package.sh` compares it with this exact list, and on Linux checks that both programs are executable. CI runs both on every pull request, so a package that would ship without a library, or with a stray file, fails there first. A change to what the app needs beside it (a new native library, say) updates the list in `build/check-package.sh` and here. The release notes are generated from the pull requests merged since the previous tag. Debug symbols, the CLI's and the app's, are kept as workflow artifacts for 30 days, not attached to the release.

A tag with a suffix, such as `v0.2.0-rc.1`, publishes a prerelease and may be on any branch. Running the workflow by hand ("Run workflow" in the Actions tab, with a version) builds and uploads the packages as workflow artifacts and publishes nothing; it is available once the workflow file is on the default branch.

## If a release goes wrong

A published release and its tag can be deleted on GitHub and the tag pushed again after a fix. Prefer a new patch version once anyone may have downloaded the release.
