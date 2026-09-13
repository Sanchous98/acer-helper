# Hand-rolled WMI COM interop, and the EC transaction gate

The app talks to ACPI-WMI (Acer gaming/profile classes, Dell's BIOS-attribute classes, standard `Win32_*` and
smart-battery queries) without `System.Management`. This document is why, the two rules that make the hand-rolled
version safe, and the process-wide lock that exists because all of it funnels into **one** embedded controller.

## Why not `System.Management`

`System.Management` relies on **classic COM interop** — runtime-generated IL marshalling stubs — which **Native
AOT cannot do**: it throws *"COM Interop is not supported on this platform"*. The whole app is Native AOT
(a prerequisite for the trimmed, self-contained build), so `System.Management` is simply unavailable.

The replacement is **source-generated COM** (`[GeneratedComInterface]`) plus **raw `VARIANT` / `SAFEARRAY`
pointers**. The source generator emits the vtable dispatch **at compile time**, so no runtime codegen is needed.
That is the same reason the app hand-rolls all its other native interop (see `docs/nvidia-gpu-oc.md`).

## Rule 1 — vtable order is load-bearing

> The methods in each `[GeneratedComInterface]` are declared in **EXACT vtable order**, taken from
> `wbemcli.h`. A method's vtable slot is **its declaration position**, after IUnknown's
> `QueryInterface`/`AddRef`/`Release`.
>
> Methods we never call are declared as **ordered placeholders purely to occupy their slot**.
>
> **DO NOT reorder, rename, or delete any method, and add new ones only in their correct `wbemcli.h` position.**
> A wrong slot is a **silent wrong-function call** — crash or garbage — invisible until it runs on real hardware.

## Rule 2 — wrap with `UniqueInstance`, or leak silently

Source-generated RCWs **do not implement `IDisposable`** — a cast to `IDisposable` is a **silent no-op**. The
only deterministic release is `ComObject.FinalRelease()`, and **that is itself a no-op unless the wrapper was
created with `CreateObjectFlags.UniqueInstance`**.

Getting this wrong does not fail loudly. Every COM reference then waits for a gen-2 GC plus finalizer pass, and
an **enumerator proxy created on the (STA) UI thread gets Released from the (MTA) finalizer thread** — a
wrong-apartment `Release` that can leak the matching server-side object **inside the WinMgmt service**.

That is why the interfaces here return **raw `nint` out-params instead of typed RCWs**: the generated marshaller
would wrap them **without** `UniqueInstance`, making deterministic release impossible. Every pointer is turned
into a callable object through one helper that wraps *and* releases the out-parameter's own reference (the
wrapper takes its own `AddRef`).

## Other conventions in the interop layer

- **All string parameters are passed as BSTR handles** (`nint`) that the caller allocates with `SysAllocString`
  and frees with `SysFreeString`. A BSTR is null-terminated UTF-16, so it also serves where the API wants a plain
  `LPCWSTR` (the `Get`/`Put` property names). `VARIANT`/`CIMTYPE` out-params are passed as **raw pointers to
  caller locals**.
- **`CoInitializeEx`**: `S_OK` / `S_FALSE` / `RPC_E_CHANGED_MODE` all mean COM is usable on this thread. We do
  **not** uninitialize (the thread keeps its apartment), and we never share proxies across threads, so the
  apartment kind does not matter.
- **`CoSetProxyBlanket` is required**, with the standard WMI auth blanket
  (`RPC_C_AUTHN_WINNT` / `RPC_C_AUTHZ_NONE` / `RPC_C_AUTHN_LEVEL_CALL` / `RPC_C_IMP_LEVEL_IMPERSONATE` /
  `EOAC_NONE`). Without it WMI calls fail with **access-denied**.
- **ExecQuery flags:** forward-only + return-immediately give a cheap in-proc enumerator; `Next` waits
  `WBEM_INFINITE`.

### The method-call gotcha that broke every Acer setter

`IWbemClassObject::GetMethod` is **invalid on an instance** (it returns an error). The in-parameter signature
must be read from the **class definition**; the *instance* is still needed, for its `__PATH`, which `ExecMethod`
runs against. Calling `GetMethod` on the instance **silently aborted every method call**.

## `PutValue`: matching the property's real CIM type

Writing an in-parameter with the wrong VARIANT shape fails **silently** — the method simply does not do what it
was asked. So the value shape is chosen from the property's declared CIM type:

| value | wire shape | why |
|---|---|---|
| `byte[]` | `SAFEARRAY` of `VT_UI1` | Acer's firmware blocks expect `uint8[]` (`uReserved[]`, …) |
| `string` | `VT_BSTR` | Dell's `BIOSAttributeInterface` `AttributeName`/`AttributeValue` are `CIM_STRING` |
| 64-bit CIM integer | **`VT_BSTR` decimal string** | WMI's own representation for `CIM_UINT64`/`CIM_SINT64`; a `VT_I4`/`VT_UI8` there is **silently rejected** — this is what broke every `Set*`/`Get*` with a `UInt64 gmInput`/`gmOutput` |
| everything else | `VT_I4` | WMI coerces it to the property's 8/16/32-bit type |

Read-back mirrors this: a 64-bit CIM integer arrives as a **decimal `BSTR`, never `VT_UI8`**, so it is parsed as
a string; a `sint64` that parsed as a negative number is reinterpreted bit-for-bit.

## `WmiSession.Gate` — one EC transaction at a time, process-wide

A WMI session is **short-lived and per-operation**, deliberately. WMI COM proxies are apartment-bound and cannot
be shared across threads, and the callers run on **both** the UI thread (sensor polling) and thread-pool threads
(set operations). Rather than marshal proxies between apartments, each call opens its own session **on the calling
thread** (CoInitialize + connect + proxy blanket) and tears it down. Connecting costs **~a few ms** — negligible
for this low-frequency use.

On top of that sits one **static** lock, taken by every `QueryFirst` and `InvokeMethod`:

> The Acer ACPI-WMI methods talk to **ONE embedded controller**, and it **does not tolerate overlapping calls**.
> A background set (a toggle) firing while the 3-second poll is mid-burst of sensor/profile reads would
> intermittently be **rejected by the EC** — *the switch moved but the hardware didn't* (user-visible as
> "sometimes doesn't apply / rolls back").

Properties of the gate that matter:

- **Static**, so it spans all sessions, threads and classes — gaming, battery and power-profile calls all hit the
  same EC.
- **Re-entrant** (`Monitor`/`Lock` semantics), so `InvokeMethod`'s own call to `QueryFirst` (to fetch the
  instance) does **not** self-deadlock.
- Held only for **its own transaction** and released in between, so reads and writes **interleave, never overlap**.
  The ~ms of blocking on the UI-thread poll is negligible.

This lock is why the refactoring plan calls the WMI path "already almost an actor": one static re-entrant lock,
one EC transaction at a time, with an explicit argument about the interleavings. Note also that it is a
**serialisation**, not a pacing/coalescing queue — the two HID `Gate`s (`EneHidController`,
`AcerEcHidController`) are the opposite and must not be merged into it (see `docs/refactoring-plan.md`).
