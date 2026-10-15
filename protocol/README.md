# Godrej aer Smart Matic: BLE protocol notes

This is the shared spec for both clients (`/macos`, `/windows`). Both payload builders
are unit-tested against [`test-vectors.json`](test-vectors.json).

## GATT

| Item           | UUID                                   |
|----------------|----------------------------------------|
| Service        | `6E400000-B5A3-F393-E0A9-E50E24DCCA9E` |
| Characteristic | `6E400003-B5A3-F393-E0A9-E50E24DCCA9E` |

The UUIDs follow the Nordic-UART-like `6E40xxxx-B5A3-F393-E0A9-E50E24DCCA9E` family.

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

### Write strategy

1. If the characteristic advertises **Write** (with response), write with response and wait
   for the ACK.
2. If that isn't supported or fails, fall back to **Write Without Response**.
3. If neither property is advertised, try with-response anyway and report the error.

### Single connection

The device accepts **one BLE central at a time**. While the phone app is connected, the
desktop clients can't connect (and the reverse). That's why both clients disconnect after
a configurable idle period (`idleDisconnectSeconds`, default 20 s; `0` = stay connected).

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
| `rI` | request index / reserved                     | 0     |

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
