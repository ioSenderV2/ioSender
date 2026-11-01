(Sample program - exercises the tool-change outline)
(Load this with the program list showing: the tree should group by tool change.)
(Air only - no Z below zero anywhere in this file.)
G21 G90 G94
G17
G54
G53 G0 Z0
(Face the top)
T1 M6
S12000 M3
G0 X0 Y0
G1 Z5 F500
G1 X100 F2000
G1 Y60
G1 X0
G1 Y0
G0 Z20
M5
(Finish contour)
(Tool: 6mm single flute)
T2 M6
S16000 M3
G0 X10 Y10
G1 Z5 F400
G1 X90 F1500
G1 Y50
G1 X10
G1 Y10
G0 Z20
M5
T3
M6
S10000 M3
G0 X50 Y30
G1 Z5 F300
G1 X55 F800
G0 Z20
M5
G53 G0 Z0
M30
