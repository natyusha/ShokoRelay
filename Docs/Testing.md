# Testing

The solution includes a small xUnit test project targeting .NET 10. Tests exercise the plugin assembly directly. Shoko Server and Plex are not required for the automated tests.

```sh
dotnet restore ShokoRelay.slnx
dotnet build ShokoRelay.slnx --no-restore -c Release
dotnet test ShokoRelay.Tests/ShokoRelay.Tests.csproj --no-build --no-restore -c Release
```

The Build & Test workflow runs these commands on Linux for pull requests targeting `master` and can also be started manually. Filesystem fixtures are created beside the test assembly, so they use the checkout's volume rather than the system temporary volume. The physical case-only filename test probes that volume and is skipped only when it is case-insensitive. It runs locally on a case-sensitive macOS volume as well as in Linux CI. Case ambiguity and selection are also covered with in-memory filenames on every platform.

## Scope

- Rule-selection tests specify expected output-to-source mappings, including independent output priority, existing final suffixes, format preference, repeated input suffixes, case ambiguity passthrough, compound suffixes, empty rules, and non-chaining behavior. Reversed directory enumeration must give the same results.
- Configuration tests cover filename-safe suffix validation, persistence of ordered rules with repeated source suffixes, rejected saves, and safe loading of invalid externally edited rules. Format preferences are checked on both load and save, including normalization, ignored invalid and unsupported entries, and defaults for older configurations. The dashboard schema must omit both subtitle options, while ordinary settings saves preserve them.
- Filesystem tests use temporary directories and the production linker and cleanup helpers. They verify symlink targets, repeated refreshes, rule and format reordering, removal of obsolete links, restoring original suffixes, unchanged source files, and exclusion of unsupported formats.

When changing these behaviors, add or update a test describing the observable output. A coverage percentage is not required. Keep test dependencies in the test project; the plugin should continue to load in Shoko without them.

## Formatting

The existing Lint & Format workflow also checks CSharpier, `dotnet format`, Prettier, and Stylelint with the Concentric configuration. Restore the pinned .NET tool before running C# checks:

```sh
dotnet tool restore
dotnet tool run csharpier check .
dotnet format ShokoRelay.slnx --verify-no-changes --severity info
```

## Manual checks

Edit the subtitle fields inside `Advanced` in `preferences.json`, following the [configuration example](../README.md#subtitle-suffix-rules). Reload the dashboard and confirm that no subtitle editor appears. Change an ordinary setting and verify that the file retains both subtitle options. Check that a VFS refresh picks up file edits without restarting Shoko, including changing the preferred format and clearing the rules to restore original suffixes.

Before release, use a small series in Shoko to refresh TV and movie VFS links, inspect the resulting source targets, and verify subtitle detection and playback in Plex. Filename selection tests cannot establish Plex language recognition or playback behavior.
