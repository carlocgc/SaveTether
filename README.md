# SaveTether

Redirects app and game save folders into one synced folder, so saves follow you between devices (for example a Windows PC and a Steam Deck). SaveTether makes the links and keeps verified backups; a sync tool of your choice, such as Syncthing, moves the folder between devices.

**Status: early development. Nothing usable yet.** Do not point it at saves you care about.

- Design: [docs/design/save-redirection-design.md](docs/design/save-redirection-design.md)
- Implementation and test plan: [docs/plans/implementation-plan.md](docs/plans/implementation-plan.md)

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
