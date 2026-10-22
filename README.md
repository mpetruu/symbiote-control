# Symbiote Control

Two toggles for Peter in Marvel's Spider-Man 2:

- **Never-ending Symbiote Surge:** activates native Surge and prevents depletion.
- **Black suit + symbiote abilities:** black appearance and all four core powers, restoring missing powers when needed. Normal cooldowns apply.

### Use

Requires Windows x64 and [.NET Desktop Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0). Supports **Steam build 24651585**, FileVersion **2.0810.0.0** (in-game **2.810.0.0**) only; other executable fingerprints are refused.

1. Back up your saves. [Download the release ZIP](https://github.com/mpetruu/symbiote-control/releases/latest) and extract the entire folder.
2. Start the game, load Peter in normal gameplay, run `SymbioteControl.exe`, then click **Attach to game**.
3. Enable either toggle and return to gameplay so queued changes can finish.
4. Disable both and wait for cleanup before saving or quitting. **Detach** or closing the menu also requests restoration; keep it open until acknowledged.

**Saves:** temporary ability grants can persist in autosaves or manual saves. Cleanup restores live state, not saves already written; crashes can prevent cleanup.

Surge requires its original perk. Loading and character switches pause the settings. Missions and cutscenes are not exhaustively tested. No game installation files are changed. The tested build passed 25 live checks, including missing-power recovery and cleanup.

### Build

With .NET SDK 10:

```powershell
dotnet publish src/SymbioteControl.csproj -c Release -o dist/SymbioteControl -p:DebugType=None
```

Copy `native/runtime` from the release into the published `native` folder. It bundles Node 24.19.0 and Frida 17.23.2; upstream notices are included. `src/native/copy-runtime.ps1` can package matching local dependencies using explicit paths.

Tests: run the published EXE with `--self-test report.json`; run `node src/native/tests/agent-lifetime.mjs` for the mocked helper lifecycle test. Neither test attaches to the game.
