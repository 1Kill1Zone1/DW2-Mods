# Cancellation Refunds 0.2.0

Cancel a new state-funded ship or orbital base from its construction queue, or use **Scuttle** while it is unfinished. The mod returns the unbuilt fraction of the recorded treasury payment. A project that cost 10,000 credits and reached 25% completion returns 7,500 credits. Use the usual cancellation actions for facilities and troop recruitment.

Each recorded payment can be refunded once, including after save/load. Private orders, projects with no recorded payment, completed or captured hulls, repairs, retrofits and combat losses are ineligible. The game handles materials normally.

## Install

Requires **Distant Worlds 2 Steam beta 1.3.8.7**, **DW2 Mod Launcher**, and **Positronic Matrix**. Close the game and launcher, copy the CancellationRefunds folder into your game's mods directory, and enable it after Positronic Matrix. Start a fresh paid order to test the refund.

Use a separate campaign slot if switching back to 0.1.1, which cannot read refund records containing pending ship payments.

## Build and verify

Build from this folder with .NET 8.

```powershell
dotnet build Source/CancellationRefunds.csproj -c Release -p:GameRoot="<path to Distant Worlds 2>" --disable-build-servers
```

See [testing](TESTING.md) and [SHA-256 checksums](SHA256SUMS).
