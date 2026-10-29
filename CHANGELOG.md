# TraceMind v0.3.1

**Download:** `TraceMind.exe` below. It's a single file and needs no install. If Windows SmartScreen warns you, click **More info → Run anyway**.

## Fixed in v0.3.1
- The window now fits your screen. On laptops or with Windows display scaling, the window could be wider than the screen, which pushed the minimize, maximize and close buttons off the edge. TraceMind now opens maximized when the screen is smaller than the window, and it remembers its size, position and maximized state between runs.

# TraceMind v0.3.0

**Download:** `TraceMind.exe` below. It's a single file and needs no install. If Windows SmartScreen warns you, click **More info → Run anyway**.

**Upgrading:** your `.tmind` projects open unchanged. TraceMind now reopens your last project on start.

## New in v0.3.0
- **Single-sided boards.** Boards can now have one copper layer, top or bottom, for CNC-milled and single-sided boards.
- **Wire links.** When a net is blocked on one layer, the autorouter can insert a jumper across the blocking track and keep routing. Jumpers use two 2.0 mm pads with 1.0 mm drill, at 0.3", 0.4" or 0.5" spacing. Use PCB → *Autoroute with wire links*, or ask Claude.
- **Router diagnostics.** When a net can't be routed, TraceMind reports exactly where it's blocked: the blocking track or pad and the coordinates of the crossing. These show in the Problems panel and are passed to Claude.
- **Per-segment track width.** Power nets keep their full width (e.g. 1.2 mm) where there's room and narrow only in tight spots.
- **Claude can draw and edit copper.** New tools let it draw tracks point by point (width per segment), move or delete tracks, change widths, add vias and add wire links. It reports clearance problems as it goes.
- **Claude can see the board.** A board view lists every track, pad, hole and unrouted connection, and can include a rendered image with mm rulers.
- **Mounting holes.** Holes have references (H1, H2…) and can be moved, resized or deleted individually, by Claude or by dragging them on the PCB.
- **CNC / fab export.** Gerbers for each copper layer and Edge.Cuts, plus an Excellon drill file in mm, all on one origin. Options: lower-left origin and mirrored bottom-side files. File → Export → *CNC milling files*, or ask Claude. If KiCad is installed, Claude can use `kicad-cli` instead.
- **KiCad.** Boards are now written in the KiCad 7 format, so KiCad 7, 8 and 9 all open them (tested against KiCad 7). *Save back to the imported KiCad file* keeps a `.bak` of the original.
- **Continuity.** TraceMind reopens your last project and autosaves unsaved work every minute. After a crash, it offers to restore that work.

## v0.2.0
- Use your Claude plan through Claude Code; ⚡ Build a board; cost/usage line; version in title bar.

## v0.1.0
- First version.
