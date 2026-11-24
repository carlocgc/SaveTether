# Security

SaveTether moves, links and deletes folders that hold people's saves, so a bug that loses or exposes data is treated as a security problem.

## Reporting

Please report it privately: on this repository's **Security** tab, choose **Report a vulnerability**. Do not open a public issue for it.

Say what SaveTether version and system it affects, and how to reproduce it. You should hear back within a week.

## What counts

- SaveTether deleting, overwriting or moving files outside the folders it was asked to manage, or without the backup it promises.
- Following a link or path out of the sync folder or a save folder (for example through a crafted folder, a mapping or a definition in a shared sync folder).
- Anything that runs code or reaches the network from data in the sync folder.
- The installer or the published packages.

A sync tool, cloud client or network share that shares the folder with someone it should not is outside SaveTether.

## Supported versions

Only the [latest release](https://github.com/carlocgc/SaveTether/releases/latest) gets fixes.
