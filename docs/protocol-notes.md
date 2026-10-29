# Protocol notes

Neither Wahoo nor Zwift documents these protocols. Everything here comes from open-source implementations and
packet captures (researched 2026-10-29). **[C]** means confirmed in code or logs; **[I]** means inferred.
The sources at the end let you re-check behaviour when firmware changes.

## KICKR CORE 2 + Zwift Cog

- **Advertisement:** name `KICKR CORE XXXX`; advertises 0x1818 (CPS) and 0x1826 (FTMS) [C].
- **Zwift service:** not advertised. It shows up after connecting as **`FC82`**. Older KICKR CORE firmware used `00000001-19CA-4651-86E5-FA29DCDD09D1` [C].
- **Zwift characteristics** (the same under both service UUIDs):
  - `…0002`: notify (riding data, logs)
  - `…0003`: write (commands)
  - `…0004`: indicate (replies)
- **Firmware:** virtual shifting needs CORE 2 firmware 2.2.44 / 3.2.44 or newer [C, Wahoo support].

### Zwift channel (what we use)

Every frame is `[type byte][protobuf]`, plaintext. Encoder: `src/MyWahoo.Core/Protocol/ZwiftTrainerProtocol.cs`.

| Step | Bytes |
|---|---|
| Subscribe | 0002 (notify) and 0004 (indicate) |
| Handshake | `52 69 64 65 4F 6E 02 01` ("RideOn"+02 01) → reply on 0004 starts with "RideOn" (4 s timeout, 3 tries) |
| Init | `41 08 05` |
| ERG | `04 18 <varint W>` |
| SIM (full) | `04 22 <len> {1: zz wind×100, 2: zz grade×100, 3: CWa×10000 (5100), 4: Crr×100000 (400)}` |
| SIM (grade only) | `04 22 <len> {2: zz grade×100}`; ≤1 Hz, re-sent every 5 s as keep-alive |
| Gear | `04 2A <len> {2: ratio×10000 [, 4: bike kg×100, 5: rider kg×100]}`: absolute chainring/cog ratio |
| Gear readback | `00 08 88 04` → `3C 08 88 04 12 … {1: {8: ratio×10000}}` |
| Riding data | `03 {1: W, 2: rpm, 3: speed×100, 4: HR}` on 0002 |

- **Sequencing:**
  - Send a SIM command before the first gear command. Gear commands are ignored until an incline has been sent [C, qz].
  - After ERG, re-send full SIM + Physical [C, TrainerHUD].
- **Gear table:** Zwift's 24 gears, ratios 0.75 … 5.49, default gear 12 = 2.40. See `GearTable.cs`. The byte patterns match captured Zwift traffic.

### FTMS fallback

Used when RideOn gets no reply, or when the user chooses it.

| Command | Bytes |
|---|---|
| Request control | `00` |
| Start | `07` |
| ERG | `05 <int16 W>` |
| SIM | `11 <int16 wind mm/s> <int16 grade 0.01%> <u8 Crr 0.0001> <u8 Cw 0.01>` |

- **Emulated gears:** grade·r, Crr·r and Cw·r³, where r = virtual / physical gear ratio. This reproduces the road force at the virtual speed [C, ORQWA ADR 0003, tested on a KICKR CORE].

### Open questions (check on hardware)

- The exact RideOn reply bytes from a CORE 2 [I].
- Whether `04 18` ERG actuates for a third-party app. Zwift does this [C], but no third-party log exists. If it doesn't, turn on *Settings → Send ERG over FTMS*.
- Whether FTMS control still works after RideOn. On a JetBlack trainer it did not [C].
- What unit `speed×100` is in. We compute speed from power instead.

## Zwift Click

- **Identification:** manufacturer data company `0x094A`, type byte `09` = Click v1, `0A` = v2 right "+" puck, `0B` = v2 left "−" puck. The name is "Zwift Click" for all of them [C].
- **Service:** v1 uses `00000001-19CA-…`, v2 uses `FC82`. Same 0002/0003/0004 characteristics. No pairing needed [C].

### Handshake (plain text, no encryption)

- **v1:** `RideOn`.
- **v2:** `RideOn 02 03`, then `00 08 00`, then `00 08 10`, 100 ms apart.
- Reply to every `FF 03…` challenge with `FF 04 00`.

### Buttons

| Model | Frame | Pressed | Released | Fields |
|---|---|---|---|---|
| v1 | `37 {1: plus, 2: minus}` | `0` | `1` | — |
| v2 | `23 {1: bitmap}` | bit **cleared** | bit set | Plus `0x1000`, Minus `0x100`, A `0x10`, B `0x20`, Y `0x40`, Z `0x80`, D-pad `0x1`–`0x8` |

- Act on press edges only.
- Each v2 puck reports only its own buttons; the left puck also relays the right puck's.

### Click v2 quirks

- **Left puck lock:** about 60 s after connecting, the left ("−") puck stops sending buttons unless the Zwift app unlocked it in the last ~24 h.
  - The lock shows up as status `FF 05 00 EA 05 … {2: 1}` [C].
  - The unlock is a server-side challenge; no open-source app can do it.
  - Workarounds:
    - B on the right puck = gear down (default mapping).
    - Optionally reboot the left puck with `18` for another ~60 s.
- **Right puck sleep:** a right puck alone sleeps about 65 s after its last button frame. Keep-alives don't help. Rebooting it with `18` after 55 s idle (zwiftboard's approach) keeps it available. The app reconnects only on a fresh advertisement; hammering a sleeping puck wedges the Windows BLE stack.

## Sources

**Trainer protocol**
- Makinolo, Zwift trainer protocol: https://www.makinolo.com/blog/2024/10/20/zwift-trainer-protocol/
- Incyclist proto: https://github.com/incyclist/devices/blob/HEAD/protos/zwift_hub.proto
- qdomyos-zwift: https://github.com/cagnulein/qdomyos-zwift (`src/devices/ftmsbike/ftmsbike.cpp`, `characteristicwriteprocessor0003.cpp`, issue #5508 = KICKR CORE 2 log)
- TrainerHUD (MIT): https://github.com/hugoBourretDesmarais/TrainerHUD
- ORQWA virtual shifting ADR: https://github.com/ORQWA/ORQWA/blob/HEAD/docs/adr/0003-virtual-shifting.md
- MaximumTrainer notes: https://github.com/MaximumTrainer/MaximumTrainer_Redux/tree/HEAD/notes

**Zwift Click**
- BikeControl: https://github.com/OpenBikeControl/bikecontrol (constants.dart, zwift_device.dart, issue #68)
- zwiftboard (Windows, Click v2 hardware-verified): https://github.com/thomaschampagne/zwiftboard
- zwiftplay (Click v1): https://github.com/ajchellew/zwiftplay
