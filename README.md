# Connected Mining 1.1.0

A Valheim mod for single-player, player-hosted multiplayer, and dedicated servers: break a mineable deposit section with your pickaxe and the touching, tool-eligible sections are mined automatically. Different resource types and adjacent deposits can connect. Buried sections are included when their colliders are loaded and connected.

**Tested with Valheim 1.0.16. Multiplayer works when Connected Mining 1.1.0 is installed and enabled on the server and every connected player.** All participants must use the same mod version; if a player is missing the mod or has an incompatible version, automatic mining is disabled until everyone is compatible. Ordinary mining is unaffected.

Build, geometry, reservation, dispatch-state, and game-API checks pass. Live multiplayer testing was reported by the user; the automated tests are not a substitute for in-game testing.

**Automatic mining never digs, lowers, levels, or otherwise modifies the ground.** It calls the deposit's damage handler directly, not the pickaxe attack or terrain code. Your actual pickaxe swing retains its vanilla terrain behavior.

## License

This project is licensed under the MIT License. You may use, copy, modify, merge, publish, distribute, sublicense, and sell copies of the software, subject to the conditions in [`LICENSE`](LICENSE). This license applies to this project's original source and releases, not to Valheim, BepInEx, or other third-party components.

## Requirements

- Valheim **1.0.16** (tested).
- [BepInExPack_Valheim **5.4.2351**](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/), based on BepInEx 5.4.23.5. Latest listed pack checked September 25, 2026.
- No other mod dependencies. Harmony is the copy already supplied by BepInEx.
- Install **Connected Mining 1.1.0 on the server and every connected player**. This is not a server-only mod. Earlier versions cannot participate in the protocol.

## Install / use

Close Valheim, install the above BepInEx pack, and extract `ConnectedMining-1.1.0.zip` into the Valheim directory. The resulting plugin location is:

```text
D:\SteamLibrary\steamapps\common\Valheim\BepInEx\plugins\ConnectedMining\ConnectedMining.dll
```

Replace the old Connected Mining DLL when upgrading; do not leave a second copy in another plugins folder. Source code and developer tools are not needed to play.

For this local project, `Install.ps1` installs the compiled plugin and upgrades the BepInEx runtime from the downloaded official pack in `D:\ValheimModTools\BepInExPack-5.4.2351`. It backs up files it replaces under `backups`, recording an installation manifest. Existing configuration and launch settings are preserved. Use `-SkipPackUpdate` when the required pack is already installed.

Launch through your normal BepInEx-enabled game setup. Enter a world and break a deposit section with the required pickaxe. There is no activation key. In multiplayer, allow a few seconds after joining for version checks and server settings to arrive.

On startup, `BepInEx\LogOutput.log` should show:

```text
Connected Mining 1.1.0 loaded. Dedicated-server coordination enabled; automatic mining does not alter terrain.
```

## Dedicated-server installation

1. Stop the dedicated server.
2. Install BepInExPack_Valheim **5.4.2351** in the dedicated-server installation, using the pack's platform-specific server setup instructions.
3. Extract the mod ZIP into that installation. Verify this file exists:

   ```text
   <Valheim dedicated server>/BepInEx/plugins/ConnectedMining/ConnectedMining.dll
   ```

4. Install the same pack and mod version on every player's game installation.
5. Start the server through its BepInEx-enabled launch setup. On Linux, use the pack's `start_server_bepinex.sh` with your server arguments. On Windows, use your configured dedicated-server launcher with BepInEx installed beside `valheim_server.exe`.
6. Check the server's `BepInEx/LogOutput.log` for the plugin startup message. The same DLL is used on Windows and Linux; no desktop player or GUI is required by the mod's coordinator.

For a Windows server on this development machine, the installer also accepts a server directory:

```powershell
.\Install.ps1 -ValheimDir 'D:\YourServerFolder' -SkipPackUpdate
```

The installer does not create a dedicated server, configure ports, or provision its launch settings. The server must already be set up to load BepInEx.

## Multiple players mining the same deposit

- The network owner of a deposit confirms each ordinary break and snapshots connected sections.
- The host/dedicated server accepts the first chain proposal and reserves its sections using network object IDs and section indices. An overlapping proposal cannot take over the chain or replace its initiating player's tool context.
- On the same owner, break processing order determines the first proposal. Across different owners, **server acceptance order** resolves near-simultaneous proposals; this is not a comparison of clients' wall-clock timestamps.
- Other players can continue hitting the deposit normally. Sections already destroyed when their automatic command arrives are skipped.
- The server sends commands to the current network owners, which use the normal destruction/drop handlers. The server does not need the full deposit scene loaded and the mod never forcibly claims ownership.
- Commands and responses carry a world-session token, operation ID, and dispatch attempt. Duplicate completed commands reuse their saved acknowledgement. Responses from the wrong peer or a previous attempt are ignored.
- An explicit refusal before execution allows up to three dispatch attempts as ownership changes. An ambiguous timeout is **not** retried on a different owner, because the first owner may already have produced drops.
- Missing/incompatible clients disable new automatic chains until everyone completes the version handshake. Players are not kicked; ordinary mining continues.

Owner disconnects, unloaded objects, or timeouts can leave part of a chain unmined. Reservations remain for 45 seconds after a job finishes, so a cancelled chain may need that delay before a fresh break can start another. Successful destruction remains normal saved world state.

## Behavior

- Supports the game's `MineRock5` and `MineRock` deposits: ore veins, mineable boulders, scrap piles, marble and other resources implemented by those classes.
- Supports natural `Destructible` rocks with resource drops and matching prefab-name prefixes, plus boulders that transform into segmented deposits. This covers single-piece mineral nodes without treating every destructible object as a mine.
- Buildings, trees, creatures, triggers and terrain colliders are excluded.
- Only a successful player pickaxe break confirmed by the deposit's owner starts a chain. Merely hitting a deposit does not start one.
- Requires the original hit's tool tier and world-level eligibility for each additional section. Ineligible sections cannot connect two otherwise separated groups.
- Additional sections use vanilla deposit destruction and drops. No extra stamina or durability is charged; no additional weapon-skill XP is granted. Normal mining statistics/effects may still be recorded by the game's handlers.
- Automatically mined sections are processed eight per frame per executing peer by default. The server also dispatches at this rate, with at most 64 outstanding commands. Connection discovery is performed before the initial break, so the original collider disappearing does not erase the connection. Very large networks can still cause a discovery hitch and many dropped items can reduce frame rate.
- Connectivity uses collision surfaces with a **10 cm tolerance**, not the entire deposit's root bounding box. Colliders, rather than visible render meshes, determine contact. Separate sections of the same deposit are not automatically connected solely because they share a parent.
- The server coordinates both single-player and multiplayer chains. Client-side world state and pending operations are reset when changing sessions.
- Each chain is limited to 4096 reserved sections, including its source. Oversized initial graphs are rejected; expansion into replacement prefabs stops at the limit. Only loaded collider geometry can be discovered.
- Buried loot follows the game's normal drop positioning; some drops can remain underground.

## Configuration

Generated after the first launch, on both server and clients:

```text
BepInEx\config\tristan.valheim.connectedmining.cfg
```

| Setting | Default | Meaning |
| --- | --- | --- |
| `General.Enabled` | `true` | Enable connected mining. |
| `Mining.ConnectionTolerance` | `0.1` | Maximum collider-surface gap in metres, range 0–0.5. Set 0 for exact contact. |
| `Mining.SectionsPerFrame` | `8` | Additional sections processed per frame, range 1–64. |
| `Mining.NaturalDepositPrefixes` | `rock,stone,obsidian,mudpile,MineRock,copper,tin,silver,flametal,blackmarble,sulfur` | Classification of single-piece resource-dropping rocks. Does not affect automatic MineRock/MineRock5 detection. |

**The host/server configuration governs the session.** Its enabled flag, contact tolerance, batching limit, and natural-deposit prefixes are sent to clients. Client-local values do not override the server's values. In single-player, your local configuration is authoritative. Settings are synchronized in memory without overwriting clients' config files.

Edit while the game/server is closed, then restart.

Unreadable mesh colliders or unsupported collider shapes are skipped, rather than using oversized bounding boxes that might join unrelated rocks. A warning identifies unreadable meshes in the log. A new game update or custom deposit using a different destruction component can require additional support.

## Build and verification

Requires a current .NET SDK. From PowerShell:

```powershell
Set-Location D:\ValheimMods\ConnectedMining
.\Build.ps1
.\Install.ps1
```

The plugin targets .NET Framework 4.8 and compiles against local game and BepInEx assemblies. The check runner uses .NET 10. Paths can be supplied to `Build.ps1` through `-ValheimDir` and `-BepInExDir`. The checks use Mono.Cecil from the downloaded pack (configured in `tests\Checks.csproj`). Game assemblies are not redistributed.

The build currently passes **56 automated checks**. They cover surface contact, intersections, containment, gaps, capsule-axis intersections, world-coordinate translation, randomized solid distances, first-chain reservations, overlapping proposals, replacement reservations, ownership reroutes, wrong-peer and stale-attempt acknowledgements, duplicate results, bounded retries, non-replay of ambiguous timeouts, game/network API signatures, runtime dependencies, and absence of terrain/attack/stamina/skill/forced-ownership calls in the compiled plugin.

**In-game behavior is not yet verified.** The build and static/geometry checks do not replace an in-game test. First-launch checks:

1. Confirm the plugin startup message and no Connected Mining errors in `LogOutput.log`.
2. Hit copper without breaking a section: no automatic mining should occur. Break a section: the connected eligible sections should drop normally.
3. Check that a separated nearby deposit remains and that touching deposits chain across their boundary.
4. Test stone, tin/obsidian, a muddy scrap pile, silver and marble with the appropriate tool. Check a large boulder that changes into smaller sections.
5. Compare the surrounding ground before and after the automatic chain. Only your actual pickaxe swing should be able to deform terrain.
6. Confirm no duplicate drops, no extra stamina/durability charge, and that an insufficient tool cannot mine or bridge higher-tier sections.

Dedicated-server integration checks (not yet performed):

1. Connect two clients with 1.1.0 to a BepInEx-enabled dedicated server; confirm the handshake completes without persistent inactive warnings.
2. Have both players break sections in the same deposit nearly simultaneously. Verify that one chain wins and that each section drops resources only once.
3. Repeat across touching deposits, including ones owned by different clients, and with different pickaxe tiers.
4. Continue ordinary mining while the chain runs, and test a boulder that transforms into a segmented deposit.
5. Disconnect an owner during a chain. Confirm that remaining sections may stop, but no timed-out section is replayed to produce extra loot.
6. Leave and rejoin/change worlds; verify there are no stale commands or settings from the prior session.
7. Connect a client without the mod or with 1.0.0. Confirm automatic mining becomes inactive while ordinary mining works, then restores after all participants have the matching version.
8. Verify server-controlled configuration, untouched ground, and unchanged mining cost on both clients.

## Troubleshooting

- **Only normal mining works:** confirm 1.1.0 is installed on every participant and the server, check `General.Enabled` on the server, and allow a few seconds for handshaking.
- **Part of a vein stays intact:** check pickaxe tier, collision-surface gaps, mesh-readability warnings, and owner-disconnect/timeout messages. Buried pieces must be loaded to be discovered.
- **The chain stops after a disconnect:** this is intentional when execution cannot be confirmed. After reservations expire, another ordinary break can start a fresh chain.
- **Errors or unexpected drops:** collect `BepInEx/LogOutput.log` from the server and involved clients, along with their Valheim and mod versions.

## Uninstall

Close Valheim and remove `BepInEx\plugins\ConnectedMining\ConnectedMining.dll`. You may also remove its generated configuration. Already-mined resources stay mined in the saved world.
