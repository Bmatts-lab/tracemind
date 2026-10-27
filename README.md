# TraceMind

AI-assisted PCB design for Windows. Describe a board and Claude does the rest: it researches real parts on the web, draws the schematic, places and routes the PCB, and checks it. You can edit everything by hand.

It runs on **your own Anthropic API key**, so you pay per use with no monthly subscription or usage cap. The chat panel shows a running cost estimate.

![PCB editor](docs/screenshots/05-pcb-pour.png)

## What it does

| | |
|---|---|
| **⚡ Build a board** | Type a description ("ESP32-C3 sensor board, USB-C, BME280, 40×30 mm, JLCPCB assembly"). Claude picks parts (with LCSC numbers), builds the schematic with all support parts, sizes the board, places, routes, runs DRC and fixes errors. |
| **Find parts** | Claude searches distributors and datasheets on the web and records MPN, manufacturer, LCSC number and datasheet on each part. |
| **Routing** | A built-in grid autorouter that handles clearances, vias, net classes and rip-up/retry. Claude steers it: routing order, power widths, which nets a pour handles, and layer locks. |
| **Review** | Ask Claude to review the design. It reads the full netlist and layout and checks for missing decoupling, floating pins, DRC problems and similar issues. |
| **Schematic editor** | Symbols, wires, net labels, no-connect flags, net highlighting, undo/redo, auto-tidy. |
| **PCB editor** | Placement (move, rotate, flip, lock), interactive 45° routing with vias, copper pours, ratsnest, DRC markers, 2 or 4 layers. |
| **Footprints** | Parametric library (0402–2512, SOT-23/223/89, SOIC/TSSOP/MSOP/SSOP, DIP, QFN, TQFP/LQFP, headers, JST, USB, crystals, switches…). Claude builds custom footprints from datasheet land patterns. |
| **Manufacturing** | Gerber X2 for all layers, Excellon drill (PTH and NPTH), BOM and CPL in JLCPCB format, zipped and ready to upload. |
| **KiCad** | Import and export `.kicad_pcb` and KiCad netlists (`.net`). |

## Getting started

1. Download `TraceMind.exe` from the latest release (or the `build` workflow artifact) and run it. It needs no install or .NET runtime.
2. Click **⚙ Settings** and paste an API key from [console.anthropic.com](https://console.anthropic.com). It's stored encrypted for your Windows user.
3. Click **⚡ Build a board**, or try **File → New from example**.

### Cost

Each request is billed at Anthropic's API prices. The chat header shows an estimate of the current chat's cost. To spend less, pick Sonnet or Haiku in Settings or lower the effort. Opus 5.5 at "high" gives the most careful designs.

## Building from source

Requires the .NET 8 SDK.

```
dotnet test tests/TraceMind.Tests
dotnet run --project src/TraceMind.App
dotnet publish src/TraceMind.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

`tools/Screenshots` renders the real window off-screen to PNGs. It's useful for checking UI changes on machines without a display.

## Project layout

```
src/TraceMind.Core   data model, netlist, DRC, autorouter, placer, Gerber/drill/BOM, KiCad I/O
src/TraceMind.AI     Claude agent (Anthropic C# SDK) and the design tools it calls
src/TraceMind.App    Avalonia desktop app (schematic + PCB editors, chat)
tests/               unit + integration tests (routing, DRC, exports, agent loop against a mock API)
```

## Keyboard

Schematic: `W` wire, `L` label, `Q` no-connect, `R` rotate, `M` mirror, `Del` delete.
PCB: `X` route, `V` via while routing, `R` rotate, `F` flip, `L` lock, `PgUp/PgDn` layer, `Ctrl+R` autoroute, `Ctrl+D` DRC.
Everywhere: wheel to zoom, middle or right drag to pan, `F` to zoom to fit.

## Known limitations

- Copper pours have solid connections (no thermal reliefs) and don't remove isolated islands.
- KiCad schematic files (`.kicad_sch`) aren't imported yet. Use a netlist or `.kicad_pcb`.
- The autorouter works on a grid. It's good for typical 2-layer hobby and prototype boards, but not for high-speed or length-matched routing.
