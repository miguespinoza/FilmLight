# FilmLight

FilmLight turns photographed film frames into positive images.

## Language

**Frame**:
A photographed film picture with its own calibration and develop settings.

**Develop settings**:
The current color, exposure, levels, crop, and processing choices for one frame. Unless manually overridden, a frame follows its immediately preceding frame's develop settings; the first frame starts from defaults. No approval step is involved.
_Avoid_: Accepted settings, approved recipe

**Override**:
A manual change to a frame's develop controls that makes those controls independent of its preceding frame. Calibration picks remain local even when develop controls are inherited.

**Calibration**:
Measurements of exposed backlight and, for negatives, clear film taken from the frame being developed.
