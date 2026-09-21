# TcgRig — two players, one machine, no Steam

    dotnet run --project TcgRig -c Release

Twenty-one scenarios, about forty milliseconds, exit code 0 or 1.

## What it is

`Session.cs` is the biggest and most dangerous file in the mod, and until now
none of it had ever been *run* with a second peer at the other end. Ten defects
were found in it by reading, and fixed by reasoning — the host-departure race,
the achievement-suppressing failed join, the `Substring` that throws inside a
Steam callback, the stall that reaps healthy peers. Reasoning is not running.

This rig runs it. Two (or three) real `Session` objects, a fake Steam underneath
them, and a clock the test controls — so the thirty-second peer timeout costs a
microsecond to exercise instead of thirty seconds.

Nothing is copied. The rig compiles the real `Session.cs`, `Packet.cs` and
`ITransport.cs` straight out of `TcgMultiplayer/`. If you change them, this
tests the change.

## How it hangs together

Steam sits behind two interfaces:

- `ITransport` — send, poll, accept, close. `SteamTransportBackend` forwards to
  the existing static class; `LoopbackTransport` puts the bytes on a fake wire.
- `ILobbyBackend` — identity, lobby membership, lobby data, and the seven
  callbacks. `SteamLobbyBackend` registers the real Steam callbacks and
  forwards them as plain events; `LoopbackLobby` raises them from a simulated
  lobby server.

`Session` takes both in its constructor. The parameterless constructor gives you
the real pair, so nothing about a normal session changed.

`LoopbackWorld` owns the virtual clock, the lobbies and the wire. You can set
latency, jitter, packet loss on the unreliable channels, and reordering; cut a
peer's cable; destroy a lobby without telling anyone; and raise a Steam
disconnect or a session failure on demand.

## Running it

    dotnet run --project TcgRig -c Release              # the suite
    dotnet run --project TcgRig -c Release -- -v        # with every log line
    dotnet run --project TcgRig -c Release -- --seed 42 # different random draw

The seed only affects the lossy and jittery cases. It has been swept across ten
seeds with no flakes.

## What a green run does NOT mean

Worth being blunt about, because a wall of PASS is persuasive and this one is
only persuasive about some things.

- **It does not prove Steam delivers a byte between two houses.** The transport
  under test is a list in memory. NAT traversal, relay fallback, and Steam
  deciding two people cannot reach each other are all outside it.
- **There is only one world.** Machines, wallets, unlocks and the visit/restore
  logic live in the Unity scene, and none of that is loaded here. This tests the
  session and the wire, not the game.
- **Physics replication and spectating are untested.** So is anything to do with
  frame rate.
- **`AcceptSession` cannot fail** in the fake, and RTT is not simulated.

M8 — two real players over real Steam — is still open, and this does not close
it. What it does close is everything that can go wrong before the first byte
leaves the machine.

## Things it has already caught

- A stray byte decoding as `Op.Bye` silently dropped a live peer, and the
  re-tracked peer came back with its malformed-packet rate limiter reset. Bye is
  now only honoured from a peer that has completed the handshake.
- Confirmed, rather than assumed, that a jump of more than half the sequence
  space reads as backwards — which is correct, and is not what I expected when I
  wrote the test.
- Random noise with a plausible opcode and enough bytes parses "successfully"
  into nonsense rather than being rejected: the wire format has no length or
  checksum field. Harmless between trusted peers on the same version, which the
  version check already enforces, but worth knowing.
