# NKeeper Wake-on-LAN plugin

The official Wake-on-LAN plugin for [NKeeper](https://github.com/OpenCraft-Solutions/NKeeper) 2.0. It wakes a host with a magic packet, from a "Wake" entry in the host's Connect menu.

- **ID:** `wake-on-lan`. It needs SDK v1 (`>=1.0.0 <2.0.0`).
- **Service type** `wake-on-lan`, "Wake-on-LAN", UDP port 9 by default, with one required setting: the host's MAC address. A host gets Wake-on-LAN by having this service.
- **Run action** `wake-on-lan.wake`, "Wake", on `wake-on-lan` services. It needs no credential.
- **Endpoint** `POST /api/p/wake-on-lan/hosts/{hostId}/wake` with `{ "serviceId": "…" }`. It needs Use on the host. It sends the magic packet (six `0xFF` bytes, then the MAC address 16 times) over UDP to the service's port, from the NKeeper server, to:
  - the local broadcast address;
  - for a service with an IPv4 address, its `/24` subnet's broadcast address and the address itself, for routers that forward directed broadcasts or keep a static ARP entry.

  A MAC address that isn't six hexadecimal bytes (separated by `:` or `-`) is refused with `400`.
- **Audit:** it records `wake-on-lan.send`, with the MAC address and where the packet went.
- It has no frontend: the shell draws the menu and shows the answer.

The design is in NKeeper's [architecture.md §7.15](https://github.com/OpenCraft-Solutions/NKeeper/blob/main/docs/architecture.md), and how plugins are written in [plugin-authoring.md](https://github.com/OpenCraft-Solutions/NKeeper/blob/main/docs/plugin-authoring.md).

## Layout

```text
backend/          the plugin: WakeOnLanPlugin.cs, the manifest (nkeeper-plugin.json) and its project
tests/            the plugin's tests, which start NKeeper's test server with the plugin's package loaded
nuget.config      NuGet sources: GitHub Packages for NKeeper's packages, nuget.org for the rest
.npmrc            the npm registry for @opencraft-solutions (the plugin has no frontend today)
.github/workflows ci.yml and release.yml
```

The plugin builds against NKeeper's **published** packages, never its source (architecture.md §9): `NKeeper.Abstractions` (the SDK), `NKeeper.Plugin.Build` (builds, packs and signs the plugin) and, in the tests, `NKeeper.Testing` (the test server and its fixtures). They share the SDK's version, set once in `Directory.Packages.props`.

## Building

You need the .NET SDK in `global.json`. GitHub Packages needs a token even to read public packages. Create a [personal access token](https://github.com/settings/tokens) that can read packages (`read:packages`), and keep it in your environment, never in a file in this repository:

```sh
export NUGET_USERNAME=<your GitHub user name>
export NUGET_TOKEN=<the token>
dotnet restore
dotnet build
```

Building packs the plugin and signs it with a development key, created on first use in `.dev/keys/` (it is git-ignored). The package, `wake-on-lan-<version>.nkp`, is written to `artifacts/plugins/`. To load it in a local NKeeper server in `Development`, point `Plugins:Directory` at `artifacts/plugins` and `Plugins:DevelopmentTrustDirectory` at `.dev/keys`.

Package versions are pinned in `packages.lock.json` files. After changing a dependency, run `dotnet restore --force-evaluate` and commit the result.

If a plugin gains a frontend, it goes in `frontend/`, depends on `"@nkeeper/sdk": "npm:@opencraft-solutions/nkeeper-sdk@^1.0.0"`, and is built by `NKeeper.Plugin.Build`. For `npm`, set `NODE_AUTH_TOKEN` to the same kind of token.

## Testing

```sh
dotnet test
```

The tests start NKeeper's test server (from `NKeeper.Testing`) as a separate process, with the plugin's package from `artifacts/plugins` loaded, and a PostgreSQL database in a container. **Docker must be running.**

## Continuous integration

`ci.yml` runs on every push to `main` and every pull request. It restores in locked mode with the workflow's token, checks formatting, builds, runs the tests and packs the plugin signed with a development key. That package is uploaded as an artifact for inspection. It is never a release.

## Releasing

Only a tag releases the plugin, and only the lead engineer tags.

1. Set `version` in `backend/nkeeper-plugin.json`, and merge it to `main`.
2. Tag the merge commit `v<version>` (for example `v0.1.0`) and push the tag.
3. `release.yml` runs in the `release` environment, so an owner has to approve it. It then:
   - checks that the tag matches the manifest's version;
   - restores, builds and tests the plugin;
   - packs `wake-on-lan-<version>.nkp` and signs it with the official key, `NKEEPER_RELEASE_KEY` (a PEM, held by the environment only). The key is written to memory-backed storage for the signing step alone, removed when the step ends, and never printed;
   - publishes a GitHub release with the package and its SHA-256.

NKeeper's installer takes the release from `deploy/plugins.lock` in the NKeeper repository, so a new release reaches installs when that file is updated there.

## License

[MIT](LICENSE).
