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
- Configuration tests cover filename-safe suffix validation, persistence of ordered rules with repeated source suffixes, rejected saves, and safe loading of invalid externally edited rules.
- Preview request tests exercise MVC validation of series IDs without running a server.
- Filesystem tests use temporary directories and the production linker and cleanup helpers. They verify symlink targets, repeated refreshes, rule reordering, removal of obsolete links, restoring original suffixes, and unchanged source files.

When changing these behaviors, add or update a test describing the observable output. A coverage percentage is not required. Keep test dependencies in the test project; the plugin should continue to load in Shoko without them.

## Formatting

The existing Lint & Format workflow also checks CSharpier, `dotnet format`, Prettier, and Stylelint with the Concentric configuration. Restore the pinned .NET tool before running C# checks:

```sh
dotnet tool restore
dotnet tool run csharpier check .
dotnet format ShokoRelay.slnx --verify-no-changes --severity info
```

## Manual checks

For dashboard changes, check that complete, valid edits save when leaving a field, and that reordering and removal save immediately. Partial or invalid edits must leave saved rules intact. Check rapid consecutive moves, edits during a pending save, save failures and retry, and persistence after reloading. Include a change to another setting while a rule save is pending to verify configuration requests preserve their order. Preview requests must not save settings or modify VFS files or the blueprint cache; the usual field-change autosave may run when clicking Preview moves focus away from an edited field.

Drag rules by their three-line handles in both directions, including across several rows. Only dropping in a different position should save; hovering, returning to the same position, pressing Escape, and dropping outside the list must not change the order. Verify the insertion marker and that suffix text remains selectable. The Move up and Move down buttons remain available for keyboard use and touch browsers without native drag support.

Before release, use a small series in Shoko to refresh TV and movie VFS links, inspect the resulting source targets, and verify subtitle detection and playback in Plex. Filename selection tests cannot establish Plex language recognition or playback behavior.
