# Licensing & automatic updates — design

Status: **phase 1 built** (22/09/2026, see §10) — the rest is design. Covers both products — Γέφυρα Καταλόγου
(this repo) and ΔΙΑΧΕΙΡΙΣΗ (`mavridis9/LIGHTHOUSE_TOOL`) — because they will share one server
and one client library. Read `CLAUDE.md` first for the module structure this depends on.

---

## 1. Goals / non-goals

**Goals**
- A shop runs only what it paid for: two modules, `catalogue` and `orders` (see §2).
- A shop never stops working because the internet is down: offline grace.
- New versions reach every shop without a visit: check → download → verify → swap.
- You can see the fleet: which shop runs which version, when it was last alive, licence state.
- Nothing about the shop's data leaves the shop: no database credentials, no customers, no
  orders — only the identifiers in §3.

**Non-goals**
- DRM-grade protection. The target is "honest shops stay honest, a copied exe on a second
  machine stops after the grace period" — not resisting a determined reverse-engineer.
- Remote configuration of the apps. The server tells the client *what it may do* and *what
  version exists*; it never changes settings.
- Per-user seats. One licence = one installation (one machine, one database).

---

## 2. Products and modules

| product id      | app                | modules                          |
|-----------------|--------------------|----------------------------------|
| `catalogbridge` | Γέφυρα Καταλόγου   | `catalogue`, `orders`            |
| `lighthouse`    | ΔΙΑΧΕΙΡΙΣΗ         | `core` (everything it does today)|

**`catalogue`** = §1-A of CLAUDE.md: sync, diff, EFIC editor, sizes, stock, guards, reset.
Standalone.

**`orders`** = the order/customer correlation and everything that rides on it: the trigger
patch (B), Πελάτες (C), till orders → history (D), cancel-in-both (E). **C, D, E are never
licensed separately** — the app already refuses them without the trigger patch; the licence
says the same thing one level up.

Future features slot under one of the two. `orders` is where auto-cancel P2O → Orexsys,
order-status feedback and loyalty will go; `catalogue` is where extras, photos and store
availability will go.

---

## 3. Identities

- **Licence key** — what you give the shop. Human-typeable, product-prefixed:
  `GK-7F3A-K2M9-Q8W1-D4TX` (Γέφυρα Καταλόγου), `DX-…` (ΔΙΑΧΕΙΡΙΣΗ). 16 base32 chars, last 4
  are a checksum so a typo is caught before any network call. The key is an *opaque id* — it
  carries no rights itself; rights live on the server and arrive in the token.
- **Installation fingerprint** — SHA-256 of: Windows `MachineGuid`
  (`HKLM\SOFTWARE\Microsoft\Cryptography`) + system-volume serial. Stable across reboots
  and app reinstalls; changes when the shop replaces the PC (then: deactivate old, activate
  new — §5). Sent as hex; the raw values never leave the machine.
- **Database fingerprint** (informational only, `catalogbridge`): the Firebird database
  file name + creation date + `STOR.NAME`. Lets the fleet view say "this is STOA", nothing
  more. Never used for enforcement — Orexsys databases get restored/moved legitimately.
- **Store id** (`catalogbridge`): `ApiStoreId` from settings. Informational, and the one
  field the server *may* bind a licence to (a licence for 1046 should not be running against
  1110), decided per licence (`bindStore`).

---

## 4. The licence token

Issued by the server, cached by the client, verified **offline** with a public key embedded
in the exe. Format: `base64url(payload) + "." + base64url(signature)`. **Built with ECDSA P-256 /
SHA-256 (IEEE P1363)** rather than Ed25519: it is in .NET itself, so the single-file exe carries
no crypto package; same security class, same key handling. The payload:

```json
{
  "v": 1,
  "product": "catalogbridge",
  "licence": "GK-7F3A-K2M9-Q8W1-D4TX",
  "installation": "9f2c…e1",            // fingerprint the token is valid for
  "customer": "ΣΤΟΑ ΑΕ",                // shown in Ρυθμίσεις only
  "modules": ["catalogue", "orders"],
  "store": 1046,                        // optional; null = any store
  "issued": "2026-09-22T10:00:00Z",
  "valid_until": "2026-10-22T10:00:00Z",   // min(last heartbeat + 30 days, licence_until)
  "licence_until": "2027-09-22T00:00:00Z", // the commercial end date, shown to the shop
  "grace_days": 14
}
```

**Verification (client, every start and before every gated action):**
1. signature valid with the embedded public key; `product` matches; `installation` matches
   this machine → otherwise **no licence** (the token is not this machine's).
2. `now < valid_until` → **licensed**, modules as listed.
3. `valid_until ≤ now < valid_until + grace_days` → **grace**: everything works, the dashboard
   shows an amber «Η άδεια δεν επιβεβαιώθηκε εδώ και N μέρες» tripwire.
4. beyond that → **expired**: see §4.1.

`valid_until` slides forward with every successful heartbeat (§5), so an installation that
talks to the server once a month never sees step 3. An installation that is cut off gets
30 + 14 days before anything stops. `licence_until` is what the *commercial* licence says,
and `valid_until` is **capped at it** (owner's decision, 22/09/2026, built in the phase-2
server): every token is issued with `min(now + 30 days, licence_until)`, so when the
commercial date passes the 14-day grace starts on that date itself, never later. After it the
server re-issues the same `valid_until` unchanged (heartbeats still answer, state `lapsed`),
a fresh activation is refused, and extending the licence on the server restores everything at
the next heartbeat — the shop sees the date coming in Ρυθμίσεις for a year, not as a surprise.

### 4.1 Enforcement

Proposed (not yet decided by the owner): **expired stops the automatic services and new writes; it never touches
what is already in the databases and never removes the trigger patch.**

| state     | `catalogue`                         | `orders`                                        |
|-----------|-------------------------------------|-------------------------------------------------|
| licensed  | everything                          | everything                                      |
| grace     | everything + amber tripwire         | everything + amber tripwire                     |
| expired   | Σύγκριση / Αντιστοιχίσεις **read-only**; Συγχρονισμός, stock sync, reset **off** | Πελάτες read-only; till-order sync **off**; send / cancel / new customer **off**; trigger patch **left as is** |
| no token  | as expired                          | as expired                                      |
| module not in token | screens hidden from the nav | screens hidden from the nav; the «Αντιστοίχιση παραγγελιών με e-mail» switch refuses to *apply* (Remove stays available) |

One function decides all of it: `License.Allows(Module)` returns `Licensed | Grace |
Expired | NotLicensed`, and every existing check (`ShowCustomers`, the till-sync switch, the
send button's `ReadState` check, `EnsureEmailCorrelation` at start) reads it. The
per-screen checks that exist today stay — they guard *technical* preconditions (trigger
patched, API configured) — the licence check sits in front of them.

**Trigger ownership.** ΔΙΑΧΕΙΡΙΣΗ applies the same V1 patch at LIGHTHOUSE. An expired
`orders` licence on Γέφυρα must not remove a patch ΔΙΑΧΕΙΡΙΣΗ relies on, and vice versa.
Rule: **licence state never triggers a removal; removal is only ever the explicit button.**
Which product "owns" the trigger at a given shop is a server-side note per installation
(`trigger_owner`), informational, shown in the fleet view — not enforced by the apps.

---

## 5. Server API

Small ASP.NET Core minimal API, HTTPS only (Caddy in front for certificates), SQLite to
start (one VPS, tens of shops), Postgres if it ever needs more. Every response the client
acts on is **signed** (token, manifest) so a compromised server or a MITM cannot grant
rights or push an unsigned exe — TLS protects privacy, the signature protects trust.

| method | path | who | purpose |
|--------|------|-----|---------|
| `POST` | `/v1/activate` | client | `{product, licence, installation, appVersion, dbFingerprint?, store?}` → `{token}` or `409 already-activated-elsewhere` (with the other machine's activation date) |
| `POST` | `/v1/heartbeat` | client | `{product, licence, installation, appVersion, store?}` → `{token}` (refreshed) — daily, and on start if the last one is > 20 h old |
| `POST` | `/v1/deactivate` | client | frees the licence for a new machine (Ρυθμίσεις → «Αποδέσμευση άδειας»); also possible from the admin side when the old PC died |
| `GET`  | `/v1/updates/{product}/{channel}` | client | the signed manifest (§7); anonymous — no licence needed to *see* that an update exists |
| `GET`  | `/v1/updates/{product}/{channel}/{version}/CatalogBridge.exe` | client | the binary (or a redirect to the GitHub Release asset) |
| `POST` | `/admin/licences` | you | create: product, customer, modules, `licence_until`, `bindStore` |
| `PATCH`| `/admin/licences/{key}` | you | extend, add/remove module, suspend, note `trigger_owner` |
| `GET`  | `/admin/installations` | you | the fleet: product, customer, version, last heartbeat, state, store, DB fingerprint |
| `POST` | `/admin/releases` | release script | register a version: channel, sha256, size, notes, `minVersion` |

Admin endpoints are behind a long random bearer key kept only on your machine; the first
admin UI is the CLI (`curl`/PowerShell) — a web page comes when the list is long enough to
need one.

**Rate/abuse:** activate is limited per key (5/hour); heartbeat per installation
(1/hour). Nothing here is worth attacking, but a stuck client loop should not fill the log.

### 5.1 Data model

```
licences        key PK, product, customer, modules (json), licence_until, bind_store,
                suspended, created_at, note
installations   id PK, licence FK, fingerprint, first_seen, last_seen, app_version,
                db_fingerprint, store, trigger_owner, deactivated_at
heartbeats      installation FK, at, app_version, state   (last 90 days, for the fleet graph)
releases        product, channel, version, sha256, size, url, notes, min_version, published_at
```

One licence ↔ one *active* installation. Activating from a second fingerprint while the
first is active answers 409; the shop (or you) deactivates the old one first. Reactivating
the *same* fingerprint is idempotent (reinstall, restored PC).

---

## 6. Client flow

**Files** (next to the exe, like everything else): `licence.dat` = the token, plain text
(built that way in phase 1: it holds no secret — it is signed and only valid on that machine —
and a plain file is what a shop can e-mail back when something is wrong). `settings.ini`
gains nothing; the licence key is inside the token and shown in Ρυθμίσεις.

**Start-up** (`App.OnStartup`, before the main window):
1. Read `licence.dat`, verify offline (§4) → state per module. Never blocks on the network.
2. If no token, or last heartbeat > 20 h: heartbeat in the background; on success replace
   `licence.dat`. Failure is logged (`licence.log`) and otherwise silent — the offline token
   decides.
3. Update check in the background (§7), 10 s after the window is up, then every 24 h.

**Ρυθμίσεις → «Άδεια χρήσης»** (new section, PIN-gated like the rest): the key, the customer
name from the token, the modules, `licence_until`, the last successful heartbeat, the state;
buttons «Ενεργοποίηση» (typed key → activate), «Έλεγχος τώρα» (heartbeat), «Αποδέσμευση».

**Dashboard**: one line under the version — «Άδεια: ενεργή έως 22/09/2027» — and the amber
(grace) / red (expired) tripwire in the same place the other red tripwires live.

**Errors the shop can see** (all Greek, all specific): key checksum wrong; key unknown; key
suspended; already activated on another machine (date); server unreachable (with "the app
keeps working until <date>"); token for another machine (after a disk clone).

---

## 7. Automatic updates

### 7.1 Manifest

`GET /v1/updates/catalogbridge/stable` → signed the same way as the token:

```json
{
  "v": 1,
  "product": "catalogbridge",
  "channel": "stable",
  "version": "1.2.0",
  "published": "2026-10-01T09:00:00Z",
  "url": "https://updates.example.gr/v1/updates/catalogbridge/stable/1.2.0/CatalogBridge.exe",
  "sha256": "…",
  "size": 69408302,
  "min_version": "1.1.0",
  "notes": "Ακύρωση παραγγελίας και στα δύο συστήματα· …"
}
```

- `version > running` → **available**: blue banner on the dashboard «Διαθέσιμη ενημέρωση
  1.2.0 — Εγκατάσταση» with the notes; installs when the shop clicks. Patch versions
  (1.1.x → 1.1.y) may auto-install at next start without asking — decide per release with a
  flag `auto: true`; default false until the mechanism has run cleanly for a while.
- `running < min_version` → **required**: the same banner, red, and the app refuses to
  *write* (sync, send, cancel) until updated. For when a version has a bug that damages data.
- Channels: `stable` for shops, `beta` for your own machine and one friendly shop; set in
  settings, default `stable`.

### 7.2 Install

The app is a single-file self-contained exe that cannot overwrite itself while running.
Sequence, all inside the app (no separate updater to keep in sync):

1. Download to `updates\CatalogBridge-1.2.0.exe.partial` (resumable; the shop's line is slow).
2. Verify `size`, `sha256`, and that the manifest signature was valid. Anything off → delete,
   log, banner says «Η λήψη ήταν ελαττωματική, θα ξαναδοκιμάσει αύριο».
3. Rename `.partial` → `updates\CatalogBridge-1.2.0.exe`.
4. On «Εγκατάσταση» (or at next start for `auto`): rename running `CatalogBridge.exe` →
   `CatalogBridge.prev.exe` (allowed while running on Windows), move the new one into place,
   start it with `--updated-from 1.1.1`, exit.
5. The new exe on start: if `--updated-from` is present, runs its self-check (open the DB,
   read settings, verify the licence). Success → log «Ενημέρωση 1.1.1 → 1.2.0 ΟΚ», keep
   `.prev.exe` for one week, then delete. Failure → rename itself away, restore `.prev.exe`,
   restart it with `--rollback-from 1.2.0`; the old version shows a red tripwire «Η
   ενημέρωση 1.2.0 απέτυχε και αναιρέθηκε» and the next check skips that version.
6. Settings, registries (`sent_till_orders.txt`, `customer_invites.txt`), logs and backups
   are untouched — they live next to the exe and are never part of a release.

A crash *later* (not at self-check) is what «Επαναφορά προηγούμενης έκδοσης» in Ρυθμίσεις is
for: swaps `.prev.exe` back by hand. Keeping exactly one previous version is enough.

### 7.3 Release script

`scripts/release.ps1` today: tests → publish → `deploy\` → tag → push → GitHub Release with
the exe. It gains, after the GitHub Release step:
- compute sha256 + size of the exe;
- `POST /admin/releases` with channel (`-Channel stable|beta`), version, sha256, size, the
  GitHub asset URL, `min_version` (`-MinVersion`, default unchanged from the previous
  release), notes (the CHANGELOG section);
- the server signs the manifest with the private key **that lives only on the server**;
  the script never sees it. Compromising your laptop does not let anyone sign a release.

Code-signing certificate (Authenticode) is a separate, recommended step: it stops SmartScreen
warning on every new version at every shop. It is not what the update check trusts — that is
the Ed25519 signature — but it is what Windows trusts.

---

## 8. Keys and secrets

| what | where | never |
|------|-------|-------|
| Ed25519 **private** key (tokens + manifests) | server only, file with 600 perms, backed up offline once | on your laptop, in git, in the release script |
| Ed25519 **public** key | embedded in both apps as a constant | — |
| admin bearer key | your password manager; `$env:UPDATES_ADMIN_KEY` when running the release script | in git |
| licence token | `licence.dat` next to the exe, DPAPI | in settings.ini, in logs |
| DB credentials, P2O credentials | `settings.ini` (as today) | on the server, ever |

Key rotation: the apps embed a *list* of public keys from day one, so a second key can be
added in a release and the first retired in a later one without a flag day.

---

## 9. Failure modes (what the shop experiences)

| situation | behaviour |
|-----------|-----------|
| Server down for a week | nothing changes; heartbeat retries daily; no tripwire until day 30 |
| Server down for 40 days | amber tripwire from day 30, red from day 44; the shop calls you, you fix the server, next heartbeat clears it |
| Shop replaces the PC | old token invalid there (fingerprint) → «Ενεργοποίηση» → 409 → you deactivate the old installation (or they do from the old PC first) → activate |
| Disk cloned to a second PC | second PC: token for another machine → no licence → nothing writes; clear message |
| Licence lapses commercially | shop saw the date for a year; grace as above; you extend on the server, next heartbeat restores |
| Bad release | `min_version` stays low, the version is pulled from `/admin/releases`, shops on it get the banner for the fixed version; a shop that already updated and failed self-check rolled back on its own |
| Two products at one shop, one licence expired | only that product's writes stop; the trigger patch stays for the other |

---

## 10. Phases

1. **Client-side licence, no server** — BUILT 22/09/2026 (`LicenseToken.cs`, `License.cs`,
   `tools/LicenceSigner`, «Άδεια χρήσης» in Ρυθμίσεις, gates in every write path, 8 tests). `License` class, token format, offline
   verification, `Allows(Module)` wired into the existing checks, Ρυθμίσεις section, dashboard
   line. Tokens signed by a tiny console tool on your machine (`sign-licence.exe`) — this is
   the "manual licensing" you can ship *now*; the server later only automates issuing.
2. **Server: activate / heartbeat / fleet** (2–3 days + VPS). Move the private key there;
   the console tool becomes the admin CLI.
3. **Updates** (2 days): manifest endpoint, release-script step, download/verify/swap/rollback,
   banner. Tested on the demo PC through two real releases before any shop sees it.
4. **Polish**: fleet page, heartbeat graph, `auto` patch installs, code-signing certificate.

Same phases apply to ΔΙΑΧΕΙΡΙΣΗ with product `lighthouse`, module `core`, reusing the client
library as a shared project (the two repos already share `EfomTriggerGuard`, `PointToOrderPhone`,
`EmailValidator` by copy — the licence client would be the first thing shared as a package).

---

## 11. Open decisions (need the owner)

1. **Domain and hosting** for the server (`updates.<your-domain>`); a €5/month VPS is enough.
2. **Grace numbers**: 30 days token validity + 14 days grace proposed. Shorter makes piracy
   harder, longer makes you less of a single point of failure.
3. **`auto` patch installs** on by default, or always ask? Proposed: ask, until phase 3 has run
   clean for a month.
4. **Bind licences to the store id** by default (yes proposed — it is the one field that
   separates the demo store from a real one).
5. **Code-signing certificate**: worth ~€200–400/year to stop SmartScreen? Proposed yes once
   there are more than a couple of shops.
6. Whether ΔΙΑΧΕΙΡΙΣΗ is licensed at all, or just gets the update mechanism.
