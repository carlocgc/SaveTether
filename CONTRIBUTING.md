# Contributing

Thanks for helping. SaveTether moves and links folders that hold people's saves, so changes are kept small and checked carefully.

## Reporting a problem

Open an issue with the SaveTether version, the system (Windows, Linux, Steam Deck), what you did, and what happened. The output of `savetether status` helps. A security problem goes through [SECURITY.md](SECURITY.md) instead, not a public issue.

A game that SaveTether does not know: say which store or launcher it comes from, and where it keeps its saves on each system.

## Making a change

- Branch from `development` and open the pull request against `development`. It is squash-merged once CI passes on Windows and Linux (x64 and arm64).
- One change per pull request; no unrelated refactoring or reformatting.
- No new dependencies without asking first.
- Before opening it: `dotnet build -warnaserror` and `dotnet test`.
- New logic gets a test. Every delete, move or link goes through `SafeFs` (a test fails otherwise).
- All logic lives in `src/SaveTether.Core`; the CLI and the desktop app only call it.
- Docs change with the code: [docs/cli.md](docs/cli.md) for a command or option, [docs/cli-json.md](docs/cli-json.md) for any `--json` change, and the [desktop app specification](docs/design/desktop-app-spec.md) for a screen.

The [design](docs/design/save-redirection-design.md) says what SaveTether guarantees, and [hardware testing](docs/testing/hardware-testing.md) how a change is tested on real machines. [Building](README.md#building) is in the README.

## Licence

Contributions are under the [MIT licence](LICENSE), as the rest of the project.
