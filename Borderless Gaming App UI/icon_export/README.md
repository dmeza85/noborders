# NoBorders — app & tray icon (option 6a "Breakout")

Mark: three corner brackets = the border being shed; the solid rounded rect = the window
already grown past it and off the canvas; the lighter top band = the title bar it loses.

## Files
- noborders-256-*.svg — full-detail mark (viewBox 0 0 64 64). Use at 24 px and above.
- noborders-16/20/24/32-*.svg — pixel-grid variant (viewBox 0 0 16 16), whole-pixel geometry only.
  **Use this one for the tray at 16 and 20 px** — the 64-grid mark goes soft below 24 px.
- noborders-tile-256.svg — desktop / taskbar tile, dark mark on the violet plate.
- png/ — rasterized 16, 20, 24, 32, 48, 256 for building the .ico.

## Colors
- Violet accent  #8b6cff  (app tile, in-app use)
- Mono light     #e8e8ee  (dark taskbar tray)
- Mono dark      #2f2f3a  (light taskbar tray)
- Tile plate     #8b6cff with mark in #0a0a0c

Windows tray does not tint for you — pick the mono asset from the current
taskbar theme (SystemUsesLightTheme) and re-load on WM_SETTINGCHANGE.

## Tray states
Same silhouette always; state is tint + one badge dot.
- Enforcing        mono at full strength (#e8e8ee / #2f2f3a)
- Paused           mono at ~45% (#6d6d7d)
- Needs elevation  full strength + 8x8 dot #ff6b74 bottom-right, 1.5 px taskbar-colored ring

## .ico
Pack 16, 20, 24, 32, 48, 256 (256 as PNG-compressed). 16/20 from the 16-grid source,
24 and up from the 64-grid source.
