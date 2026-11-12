# SaveTether

Redirects app and game save folders into one synced folder, so saves follow you between devices (for example a Windows PC and a Steam Deck). SaveTether makes the links and keeps verified backups; a sync tool of your choice, such as Syncthing, moves the folder between devices.

**Status: early development.** The command line works and is tested, but it has not yet been tried on real devices. Do not point it at saves you care about.

- Design: [docs/design/save-redirection-design.md](docs/design/save-redirection-design.md)
- Implementation and test plan: [docs/plans/implementation-plan.md](docs/plans/implementation-plan.md)

## Using it

On the first device, whose saves become the shared copy:

```bash
savetether init --sync-root ~/Sync --device desktop --create-root
savetether map add ppsspp.savedata --target ppsspp/SAVEDATA --source /path/to/PSP/SAVEDATA --process PPSSPPSDL
savetether apply ppsspp.savedata --copy
```

On each other device, after the sync tool has delivered the `savetether` folder:

```bash
savetether init --sync-root ~/Sync --device deck
savetether map add ppsspp.savedata --source /path/to/PSP/SAVEDATA
savetether apply ppsspp.savedata
```

If that device has saves of its own, `apply` stops, shows how they differ from the shared copy, and lists the choices. A verified backup is taken before anything is changed; see `savetether backup list`. `savetether status` shows each mapping's state, `savetether revert <id>` undoes a link, and `savetether --help` lists everything. Add `--dry-run` to see what a command would do, and `--json` for machine-readable output.

## Building

Requires the .NET 10 SDK.

```bash
dotnet build
dotnet test
```

Native binary (needs the platform's native toolchain: Visual Studio C++ build tools on Windows, `clang` and `zlib1g-dev` on Linux):

```bash
dotnet publish src/SaveTether.Cli -c Release -r linux-x64
```

Use `win-x64` or `linux-arm64` for the other targets.

On Windows, if publishing fails with `'vswhere.exe' is not recognized`, put Visual Studio's installer folder on `PATH` for that shell first:

```powershell
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
```

## Licence

MIT. See [LICENSE](LICENSE).
