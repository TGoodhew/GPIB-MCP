# Test fixtures

## test.plt

A real HP-GL/2 plot captured from an HP 8563E spectrum analyzer (graticule + trace +
marker + annotation). Used as a render-regression fixture for the
[`Hpgl.Rendering`](../src/Hpgl.Rendering/) library — see the linked GitHub issue.

Rendering it with the default options produces a correct spectrum-analyzer screen: a white
10×10 graticule on black, a cyan trace with a peak, a green marker diamond, and green
annotation (`ATTEN`, `RL`, `MKR`, `CENTER 460.000kHz`, `SPAN 1.000kHz`, `RBW/VBW 100Hz`,
`SWP 101ms`).
