# Ways to share the folder

SaveTether moves save folders into one folder, the **sync root**, and leaves links in their place. It does not move that folder between machines; something else does. This page lists the ways that can be done, what each needs, and what has been tested.

Whatever the method, two things hold:

- **One machine plays at a time.** Let the copy reach the next machine before starting there. Nothing locks the saves across machines.
- **The links stay on each machine; only ordinary files are shared.** SaveTether puts no links inside the sync root, so any tool that copies files and folders will do. Deletions are shared too, which is why moving an app's folder with a tool that follows links can empty the shared copy everywhere (see [moving an app's folder](cli.md#workflows)).

| Method | Windows | Linux, Steam Deck | Offline play | Tested |
|---|---|---|---|---|
| [A sync tool on each machine](#a-sync-tool-on-each-machine) | Yes | Yes | Yes | Yes, with Syncthing |
| [A cloud storage folder](#a-cloud-storage-folder) | Yes | Where a client exists | Yes, if files are kept on the device | No |
| [A network share](#a-network-share) | **No** | Yes | No | No |
| [One machine, no sharing](#one-machine-no-sharing) | Yes | Yes | Yes | Yes |
| [A removable drive](#a-removable-drive) | Not advised | Not advised | n/a | No |

## A sync tool on each machine

Each machine keeps its own copy of the sync root on a local drive, and a sync tool such as [Syncthing](https://syncthing.net/) keeps the copies in step. This is what SaveTether was built and tested with: a Windows PC and a Steam Deck sharing through Syncthing, with a Linux box that receives only.

- Every machine can play offline; changes travel when the machines next see each other.
- The sync tool needs to deliver every file eventually. SaveTether checks before linking that the shared copy has arrived, and stops if it looks partial.
- Other tools that keep a folder in step between machines, such as Resilio Sync, work the same way, but have not been tried.

## A cloud storage folder

A folder managed by a cloud storage client (OneDrive, Dropbox, Nextcloud, Google Drive for desktop) is also a local copy kept in step, so it works the same way. It has not been tested.

- **Keep the files on the device.** Clients that keep files "online only" until opened (OneDrive's Files On-Demand, Dropbox's online-only files, Google Drive's streaming mode) would make a game wait for a download on every save, and make SaveTether's backups and comparisons download everything. Mark the sync root to be kept on the device: in OneDrive, "Always keep on this device".
- When two machines change a file before syncing, these clients keep both as a renamed "conflicted copy". SaveTether does not merge those; pick one by hand.
- On Linux, and the Steam Deck in particular, not every service has an official client.

## A network share

The sync root is a folder on a NAS or file server (SMB or NFS), and every machine works on it directly. Nothing is copied, so nothing can be out of step, but nothing works while the share is unreachable.

**On Linux, including the Steam Deck,** this works: a symbolic link can point into a mounted share. It has not been tested on hardware.

- Mount the share at the same path every time (an `fstab` entry or a systemd mount), before the apps start.
- If the share is not mounted, every link is broken: `status` reports `Broken`, the app's tray icon turns amber, and the apps cannot load or save. There is no offline play.
- A Flatpak app needs access to the mount path. `apply` offers to grant it, for that folder only.

**On Windows, it cannot work,** and SaveTether refuses it when the device is set up. Windows links save folders with junctions, which only point to local drives; symbolic links could point to a share, but need administrator rights or Developer Mode, which SaveTether does not ask for. Mapped drive letters (`Z:\`) and `\\server\share` paths are both refused.

To use a share with a Windows machine, keep a local copy on that machine in step with the share: Syncthing running on the NAS, or the NAS maker's own sync client. Linux machines can still use the share directly.

## One machine, no sharing

Nothing requires a second machine. With one, SaveTether gathers save folders that are spread across an app's install, a Wine prefix or a Flatpak sandbox into one place, where a backup tool can cover them in one rule. Set up with `init --create-root` and apply as usual. Adding a second machine later is the usual `init` there.

## A removable drive

Putting the sync root on a USB drive and carrying it between machines is not advised. Each machine's links point to the drive's path, so while it is unplugged every link is broken and the apps cannot load or save, and a drive letter or mount point that changes breaks them too. `status` reports it, but nothing can make the links work without the drive.

A removable drive is a better fit as the place a sync tool copies to, or as a backup location for the sync root.
