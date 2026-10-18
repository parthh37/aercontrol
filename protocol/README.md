# Godrej aer Smart Matic: BLE protocol notes

This is the shared spec for both clients (`/macos`, `/windows`). Both payload builders
are unit-tested against [`test-vectors.json`](test-vectors.json).

## GATT

| Item           | UUID                                   |
|----------------|----------------------------------------|
| Service        | `6E400000-B5A3-F393-E0A9-E50E24DCCA9E` |
| Characteristic | `6E400003-B5A3-F393-E0A9-E50E24DCCA9E` |

The UUIDs follow the Nordic-UART-like `6E40xxxx-B5A3-F393-E0A9-E50E24DCCA9E` family.

### Characteristic roles (from the vendor app's `cwdtranscoderlibrary.utils.Constants`)

| UUID | Name in vendor app | Use |
|------|--------------------|-----|
| `6E400001-…` | `RESPONSE_UUID` | **replies**: subscribe to notifications here |
| `6E400002-…` | `PROVISION_UUID` | provisioning (mT 101) |
| `6E400003-…` | `ACTION_UUID` | **commands**: spray, fetch status, refill, set time… |
| `6E400004-…` | `SCHED_UUID` | schedule upload (mT 103) |
| `00010203-…2B12` | `OTA_UUID` | Telink firmware update |

### Observed GATT table (device name "Smart Matic", Telink SoC)

```
service 6E400000-B5A3-F393-E0A9-E50E24DCCA9E
  char 6E400003-…  [read, writeWithoutResponse, notify]   ← spray command
  char 6E400001-…  [read, notify]
  char 6E400002-…  [read, writeWithoutResponse, notify]
  char 6E400004-…  [read, writeWithoutResponse, notify]
service 00010203-0405-0607-0809-0A0B0C0D1912             ← Telink OTA
  char 00010203-0405-0607-0809-0A0B0C0D2B12  [read, writeWithoutResponse]
```

Field notes from the first live test (2026-10-15, macOS):

* The device advertises the name **"Smart Matic"** and does **not** include the service
  UUID in its advertisement. Scan without a hardware service filter and match on the name.
* The command characteristic supports **write without response only**.
* Attempt 1: mN=154, no notify subscription, disconnect 2 ms after the write. **No spray.**
* Attempt 2: mN=155, notifications enabled on `…0003`, 1 s settle before disconnect.
  **Sprayed.** The device sent no notification back.
* Attempt 3 (GUI): mN=154 again (after 155), connection held open about 20 s. **Sprayed.**
  So a lower or reused `mN` is accepted, and the failure in attempt 1 was the immediate
  disconnect dropping the queued write and/or the missing notify subscription.
* Both clients therefore enable notifications and wait about 1 s after each write before they
  allow a disconnect. Auto-increment `mN` stays on by default as a cheap safeguard;
  sending the *same* `mN` twice in a row hasn't been tested yet.

Later findings (2026-10-18):

* **First spray after a wake-up misfires.** The user reported that the first spray after
  connecting doesn't fire and the second one does. The macOS log shows both sprays acked
  (`{mT:154, ack:200}`) and counted (`sn[0]` went up each time), so the firmware takes the
  command but doesn't reliably fire it. The misfires were written about 120–260 ms after
  connecting to an aer that had been idle for minutes. The good ones went to an aer that had
  been awake for a few seconds. Both clients now wait until the link has been up for 2 s
  before the first write on a fresh connection (`postConnectSettle` / `PostConnectSettle`).
  Battery was 2.50 V at the time, below the vendor's 2.7 V danger level, which may make this worse.
* **The vendor app (v2.4) never writes right after connecting.** On connect it waits 2 s,
  discovers characteristics, subscribes, and requests **MTU 185**. It gets the unique id
  after 4 s, fetches status after 3 s and sets the time after 6 s. Its BLE library delays
  *every* write by 500 ms and always uses write-without-response.
* **The vendor app never writes a CCCD.** It only calls Android's local
  `setCharacteristicNotification(RESPONSE, true)`. `6E400001` has no CCCD at all: descriptor
  discovery returns ATT 0x0A, which macOS reports as "The attribute could not be found". The
  firmware notifies on it anyway. CoreBluetooth, Android and Windows all deliver those
  notifications: Windows sprays show as confirmed even though the CCCD write fails there too.
* **Status replies need a large MTU.** The spray ack is 18 bytes and fits the default ATT MTU
  of 23 (20-byte notifications). The fetch-status reply is 105 bytes, needs an MTU of at least
  108, and comes back whole only after an MTU exchange. That's why the vendor app requests 185.
  WinRT can't request an MTU, because Windows negotiates it itself, so the Windows client waits up
  to 3 s for that exchange before it sends fetch status. It logs a warning if the MTU is still 23.
  The leading explanation for missing battery and refill on Windows (v1.4.0 and earlier) is
  that fetch status went out right after connecting, before the exchange.
* **Reads don't return replies.** Every characteristic in the aer service reads `00 00`.
  Replies exist only as notifications, so polling by read isn't a workaround.

### Write strategy

1. If the characteristic advertises **Write** (with response), write with response and wait
   for the ACK.
2. If that isn't supported or fails, fall back to **Write Without Response**.
3. If neither property is advertised, try with-response anyway and report the error.

### Single connection

The device accepts **one BLE central at a time**. While the phone app is connected, the
desktop clients can't connect (and the reverse). That's why both clients disconnect after
a configurable idle period (`idleDisconnectSeconds`, default 3 s; `0` = stay connected;
settings saved with the old 20 s default are migrated to 3 s), and wait up to 30 s for a
busy device to free up when connecting.

## Messages

Every request is an indefinite-length CBOR map `{mT, mN, …}` written to `…0003` (or to
`…0004` for schedules). The device answers on `…0001` with `{mT: request+50, mN, ack, …}`,
where **`ack: 200` = OK**. The vendor app writes messages with Jackson's `CBORGenerator`,
which is where the indefinite-length `bf … ff` maps come from.

| mT | Request | Extra keys | Reply mT |
|----|---------|-----------|----------|
| 101 | provision | (written to `…0002`) | 151 |
| 102 | set time | epoch | 152 |
| 103 | set schedules | (written to `…0004`) | 153 |
| **104** | **spray now** | `rI` | **154** |
| **105** | **refill inserted** | `rR: true` resets the spray counters | **155** |
| 106 | stop schedule | | 156 |
| **107** | **fetch status** | (none) | **157** |
| 108 | remove schedule | | 158 |
| 109 | retrieve schedule | | 159 |
| 110 / 111 | set / get unique id | | 160 / 161 |
| 112 | restart schedule | | 162 |
| 113 | test LED | | 163 |
| 114 | get time | | 164 |

### Fetch status (107 → 157)

Request: `bf 62 6d 54 18 6b 62 6d 4e <mN> ff`. Reply captured from fw 3.0.6-T1:

```
{_ "mT": 157, "mN": 160, "ack": 200,
   "bV": 2506,                  battery, millivolts
   "fV": "3.0.6-T1",            firmware
   "cS": 133,                   auto-spray state: 111 or 0 = off, 112/121/131 = 10 min, 113/122/132 = 20, 114/123/133 = 40
   "sC": {"sn": [65, 0, 0, 65],     spray-now count per interval slot 0/10/20/40 min
          "sc": [2330, 263, 0],     scheduled sprays per interval 10/20/40
          "bn": [17, 88, 4]},       device-button presses per interval 10/20/40
   "sV": "101110" }             structure version
```

How the vendor app (v2.4) turns that into what it displays:

* **Sprays used** = the sum of all ten counters. **Refill level** = `round((2200 − min(used,
  2200)) / 2200 × 100)` %. One refill is rated at **2200 sprays** (`Constants.TOTAL_SPRAY`).
  Sending `{mT:105, rR:true}` after inserting a new can resets the counters.
* **Battery** is shown only as OK / danger: ≥ 2.7 V is normal, < 2.7 V shows a danger icon,
  and 2.70–2.799 V triggers a low-battery notification. AerControl also shows an
  *estimated* percentage, linear from 2.4 V (0 %) to 3.2 V (100 %).
* The vendor decoder slices the hex string at fixed offsets and has two branches for
  short and long firmware strings. AerControl decodes the CBOR generically and looks up
  keys instead, which tolerates field-length changes.

## Spray payload

The payload is a 16-byte **indefinite-length CBOR map** with three unsigned-integer
values, keys encoded in this fixed order:

```
{ "mT": 104, "mN": 154, "rI": 0 }

bf                 map, indefinite length
  62 6d 54         text(2) "mT"
  18 68            uint 104
  62 6d 4e         text(2) "mN"
  18 9a            uint 154
  62 72 49         text(2) "rI"
  00               uint 0
ff                 break
```

| Key  | Meaning (best guess)                         | Value |
|------|----------------------------------------------|-------|
| `mT` | message type; 104 = spray now                | 104   |
| `mN` | message number / sequence                    | 154 in capture |
| `rI` | repeat interval in minutes: 0 = spray once; 10/20/40 = auto-spray mode (the vendor app's on/off switch and interval button send these) | 0 |

### `mN` sequencing

The captured payload uses `mN = 154`. Live testing showed a reused `mN` is accepted
(see field notes). **Auto-increment mN** is still on by default as a safeguard. The client then sends the stored
value and persists `next(mN)` after each successful write. Integers use the shortest
CBOR encoding, so the payload length changes when `mN` crosses 23/24 or 255/256. See
the test vectors.

### Unsigned integer encoding (major type 0)

| Range              | Encoding           |
|--------------------|--------------------|
| 0 … 23             | `0x00 + n`         |
| 24 … 255           | `0x18 nn`          |
| 256 … 65535        | `0x19 nnnn`        |
| 65536 … 2³²−1      | `0x1a nnnnnnnn`    |

Text keys are always 2 bytes, encoded as `0x62` followed by ASCII.
