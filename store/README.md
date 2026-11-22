# Store screenshots

The five images for the Macro Deck Store listing, in upload order. All are
1920x1080 (16:9), which is the frame the Store gallery renders them in: it
letterboxes anything else and crops the thumbnails to 16:9, so a different
ratio either leaves bars or cuts the subject.

| File | Shows |
| --- | --- |
| `01-hero.png` | Title, the plugin icon, and four real deck widgets: CPU, CPU temperature, GPU and a fan slider. |
| `02-sensor-window.png` | The sensor window: tree, live readings, branch counts, and the `controllable` / `control below` badges. |
| `03-variables.png` | Four widgets with their variable references, and the label/action use of a templated field. |
| `04-fan-control.png` | The fan slider close up, the two fan actions, and that fan control needs PawnIO and elevation. |
| `05-remote.png` | A remote machine branch in the tree, and the key-stability note. |

The old text-only screenshot was dropped: it used the Macro Deck 2 type and
showed raw `Free / Total / Used` numbers next to the newer gauge widgets.

## Rebuilding

```
powershell -ExecutionPolicy Bypass -File store/build.ps1
powershell -ExecutionPolicy Bypass -File store/validate.ps1
```

The build uses Node (for the window mockups) and headless Microsoft Edge (the
renderer); neither is a project dependency.

- `src/01-hero.html` ... `src/05-remote.html` are the slides, sharing
  `src/slides.css`.
- `src/make-windows.mjs` reads the raw page template out of
  `ClassicWindowPage.cs` and writes `src/sensor-window.html` and
  `src/remote-window.html`. The stylesheet and row markup therefore stay the
  plugin's own; only the tree, counts and readings are example data. Re-run it
  when the window itself changes.
- `src/captures/` holds the widgets, cut once from the previous Store images.
  `src/patch-cards.ps1` redraws two labels on them: the fan card used to read
  `Fan 1 2229RPM64 %` as one string, and the GPU card labelled the hotspot
  reading `Hot sport`. The script is idempotent and runs as part of the build.

## Notes

- The readings in the sensor window are example data, not a benchmark; the
  hardware names are the ones the library would report for that machine.
- No image here is AI-generated. The previous hero was, so it could be replaced
  without keeping an AI declaration for the screenshots; the README now uses
  `01-hero.png` as well.
- Claims match `README.md`: fan control requires PawnIO and administrator
  rights, and that is stated on the fan slide. The rest of the sensors work
  without either.
- Keep the subject in the middle third: the Store thumbnails are 112 px wide
  and crop to 16:9, so an off-centre composition loses its edges before it is
  ever opened.
