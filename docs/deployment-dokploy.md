# Deploying LogoCraft Studio on Dokploy

This guide describes a **demo deployment** of LogoCraft Studio on
[Dokploy](https://dokploy.com). The repository ships a multi-stage `Dockerfile` that builds
the Fable/Vite frontend and publishes the ASP.NET Core server, so no source changes are
needed.

Dokploy is not required to be "production grade" for this app: there is no database, no
authentication and no persistent server state. The settings below are chosen so the demo is
reachable, fast to rebuild and hard to abuse accidentally. Read
[Security notes](../README.md#security-notes) in the README first.

---

## 1. Prerequisites

- Dokploy installed on a server with Docker and Traefik (the standard installation).
- The repository `https://github.com/aurora/logocraft`, branch `master`.
- A domain (or subdomain) whose DNS you control. This is strongly recommended:
  - the invite-link button uses `navigator.clipboard`, which browsers only allow in a
    **secure context** (HTTPS);
  - Dokploy's free `traefik.me` domains are HTTP-only.
- Server sizing: the **Docker build** runs the .NET 10 SDK, Fable and `npm ci`, so plan
  roughly 2 vCPU and several GB of free RAM/disk for the build. The **runtime** container
  is small (ASP.NET Core only).

## 2. Create the application

1. **Projects → Create Project**, e.g. `logocraft`.
2. Inside the project/environment, **Create Application**, e.g. `logocraft`.
3. Open the application's **General** tab.

| Field | Value |
| --- | --- |
| Source Type | `Git` |
| Provider | `GitHub` (install the Dokploy GitHub App and grant it this repository, or use `Git` with the clone URL for a public repo) |
| Repository | `aurora/logocraft` |
| Branch | `master` |
| Build Path | `/` |
| Build Type | **`Dockerfile`** |
| Dockerfile Path | `Dockerfile` |
| Docker Context Path | `.` |
| Docker Build Stage | _leave empty_ |
| Auto Deploy | on (optional, see [§7](#7-auto-deploy-optional)) |

Do **not** use `Nixpacks` or `Railpack` here. The build requires the .NET 10 SDK and
Node.js 22; both are installed inside the Dockerfile. Dokploy's generic builders do not
provide that toolchain.

The build needs outbound network access for NuGet and npm. No registry, no build arguments
and no build secrets are required.

## 3. Environment tab

The image already sets the two variables the app needs:

```text
ASPNETCORE_URLS=http://+:8080
ASPNETCORE_ENVIRONMENT=Production
```

You can leave them alone. For a public demo it is worth tightening the relay limits,
because anyone can create rooms and the server keeps each room's update log in memory:

```text
LOGO_MAX_ROOMS=50
LOGO_MAX_PEERS_PER_ROOM=12
LOGO_MAX_CONNECTIONS=64
LOGO_MAX_LOG_BYTES=2097152
LOGO_MAX_TOTAL_LOG_BYTES=33554432
LOGO_MAX_MESSAGES_PER_SECOND=20
LOGO_ROOM_TTL_MINUTES=30
```

All limits are optional and documented in the README's
[relay limits](../README.md#relay-limits) table. They are read at startup, so a redeploy or
restart is required after changing them.

### Origin check

The relay accepts same-host browser origins by default. Dokploy/Traefik passes the original
`Host` header (`passHostHeader: true`), so normally nothing has to be configured here.

Only if the WebSocket is rejected with **403** while the page itself loads, set the exact
public origin explicitly:

```text
LOGO_ALLOWED_ORIGINS=https://logocraft.example.com
```

Comma-separated for multiple origins, with scheme, **without** a trailing slash or path.
This check is not authentication — it only blocks ordinary cross-site browser connections.

## 4. Domain tab

Add one domain:

| Field | Value |
| --- | --- |
| Host | `logocraft.example.com` |
| Path | `/` |
| Container Port | `8080` |
| HTTPS | on |
| Certificate | `letsencrypt` |
| Internal Path | _empty_ |
| Strip Path | off |

Create an `A` record (and `AAAA` if you use IPv6) for the host pointing at the Dokploy
server. Certificate issuance can take a moment after DNS resolves.

Important details for this app:

- **Use `/` as the path.** The frontend references `/style.css` and `/build/App.js`
  absolutely, and the WebSocket lives at `/ws/{room}`. Sub-path deployments with
  "Strip Path"/"Internal Path" are not supported.
- **No WebSocket configuration is needed.** Traefik upgrades `wss://…/ws/{room}`
  automatically; the client uses `wss://` whenever the page is served over HTTPS. The app
  sends a WebSocket ping every 20 seconds, so normal proxy idle timeouts are fine. Do not
  put a cache or a path-rewriting rule in front of `/ws`.
- **Use HTTPS.** Besides TLS, the "Copy link" / "Copy invite link" buttons need a secure
  context to reach the clipboard API.
- **Cloudflare in front:** proxy mode on, SSL mode **Full (strict)**, WebSockets are on by
  default. Do not add a "Cache Everything" rule for the app or `/ws`.

If you only want to smoke-test, Dokploy's generated `traefik.me` domain works for HTTP —
but copy-link and other secure-context features will not.

## 5. Advanced tab

### Cluster settings

- **Replicas: `1`.** Never scale this app horizontally. The Yjs relay keeps every room's
  update log and awareness state **in the process memory**; two replicas would split a room
  between two independent relays. There is also no shared storage or Redis behind it.
- Registry: leave empty (Dokploy builds the image itself).

### Resources

Enter raw values — Dokploy passes them straight to the Docker API and does not parse units
like `MB` or `1.5`:

| Setting | Value | Meaning |
| --- | ---: | --- |
| Memory Limit | `536870912` | 512 MB |
| Memory Reservation | `134217728` | 128 MB |
| CPU Limit | `1000000000` | 1 core |

512 MB is enough for a small demo with the limits from §3. Leave additional headroom on the
host if Dokploy builds on the same machine, since the Docker build is much heavier than the
running container.

### Ports

Leave **Ports** empty when you use a domain: Traefik routes to container port `8080`
internally and the port does not need to be published on the host.

Only add `Published Port 8080` / `Target Port 8080` (TCP) if you intentionally want to reach
the demo as `http://server-ip:8080` without a domain. That bypasses HTTPS and the
secure-context features, and exposes the relay directly.

### Volumes

None. There is no database and no server-side persistence. The durable copy of each room
lives in the visitors' browsers (IndexedDB); the relay only remembers the current state
until it restarts. Volume mounts would not preserve anything useful.

### Health check

The runtime image (`mcr.microsoft.com/dotnet/aspnet:10.0`) does not contain `curl` or
`wget`. A health check that calls one of them will mark the container unhealthy and cause
restart loops. For this demo, **leave the Swarm health check empty** and rely on the
deployment logs plus the default restart policy.

If you want a container health check anyway, add `curl` to the runtime stage of the
Dockerfile first (for example `RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*`)
and then use:

```json
{
  "Test": ["CMD", "curl", "-fsS", "http://localhost:8080/"],
  "Interval": 30000000000,
  "Timeout": 5000000000,
  "StartPeriod": 30000000000,
  "Retries": 3
}
```

### Update config, restart policy, rollbacks

- **Update Config:** keep the default (`stop-first`). `start-first` / zero-downtime would
  briefly run two relays side by side. Live WebSocket state is not migrated between
  containers anyway, so zero downtime buys nothing here; reconnecting clients restore the
  room from IndexedDB.
- **Restart policy:** keep the default. A restart only clears the in-memory relay state.
- **Rollbacks:** safe to use. There is no schema or data directory that a rollback could
  corrupt.

## 6. Deploy and verify

1. Click **Deploy** and watch the build logs in the **Deployments** tab. The first build
   downloads NuGet and npm packages; subsequent builds are faster.
2. Check the **Logs** tab. The app should log that it is listening on port 8080.
3. Open `https://logocraft.example.com` in two tabs or two browsers:
   - the name prompt appears on the first visit;
   - both tabs show a green "Connected" status in the subheader;
   - typing in one editor updates the other, and the invite link joins a second browser.
4. In the browser dev tools, **Network → WS** should show `/ws/<room>` with status
   **101 Switching Protocols**.
5. Restart the application in Dokploy and reload an open tab: it reconnects and restores
   the room from IndexedDB.

### Troubleshooting

| Symptom | Likely cause / fix |
| --- | --- |
| `502 Bad Gateway` after deploy | Container not listening on `8080`, wrong Container Port in the domain, or the app failed to start — check **Logs**. |
| Page loads, WebSocket reconnects forever | Proxy does not pass the upgrade or strips the path; do not use a domain path other than `/`. |
| WebSocket answers `403` | Origin mismatch — set `LOGO_ALLOWED_ORIGINS` to the exact public origin and redeploy. |
| "Copy link" does nothing | Page is not served over HTTPS; the clipboard API needs a secure context. |
| Rooms appear empty after a redeploy | Relay state is in memory; open tabs resync from IndexedDB. Export room bundles for durable backups. |
| Build fails or the host freezes | The Docker build needs several GB of RAM; build on a larger host or in CI and deploy a prebuilt image. |
| Only one of two replicas sees the same room | Replicas are set above 1; the relay is single-process by design. Set **Replicas** to `1`. |

## 7. Auto deploy (optional)

- Turn on **Auto Deploy** in the application's **General** tab and make sure the configured
  branch is `master`. With the GitHub provider Dokploy registers the webhook itself; for
  other providers copy the **Webhook URL** from the **Deployments** tab into the repository
  settings.
- **Watch Paths** are optional; leave them empty to rebuild on every push.
- The API equivalent is `POST /api/application.deploy` with the application id and an API
  key, useful for a CI pipeline.

## 8. What this setup deliberately does not do

- **No authentication.** Anyone with a room link can read and edit that room. The ID is the
  only secret.
- **No horizontal scaling.** One process, one relay.
- **No server-side persistence.** Server restarts lose the relay state until clients
  reconnect; browsers keep the authoritative copy locally.
- **No per-IP rate limits.** The built-in caps are per process, room and connection. Put
  Traefik/Cloudflare rate limits in front of a public demo if it attracts abuse.

## References

- [Dokploy: Applications](https://docs.dokploy.com/docs/core/applications)
- [Dokploy: Build Type](https://docs.dokploy.com/docs/core/applications/build-type)
- [Dokploy: Domains](https://docs.dokploy.com/docs/core/domains)
- [Dokploy: Advanced (resources, health check, ports)](https://docs.dokploy.com/docs/core/applications/advanced)
- [Dokploy: Auto Deploy](https://docs.dokploy.com/docs/core/auto-deploy)
- [Dokploy: Zero downtime](https://docs.dokploy.com/docs/core/applications/zero-downtime)
