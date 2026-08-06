(Air-cut test program - for exercising Feed Hold / Stop / Rewind without cutting.)
(SAFETY: this file contains NO Z WORD and NO SPINDLE COMMAND, so it cannot plunge)
(and cannot start the spindle. Jog to a safe height and a clear area BEFORE running.)
(Motion is G91 INCREMENTAL and every square returns to its own start point, so the)
(machine never travels more than 20mm from where you began, however long it runs.)
(~100 moves at F600 = about 2.5 minutes. Soft limits still apply: if you start within)
(20mm of a limit it will alarm rather than move - that is the intended safe failure.)
G21 G94 G17
G91
(square 1 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 2 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 3 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 4 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 5 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 6 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 7 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 8 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 9 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 10 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 11 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 12 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 13 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 14 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 15 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 16 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 17 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 18 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 19 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 20 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 21 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 22 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 23 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 24 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(square 25 of 25)
G1 X20.000 F600
G1 Y20.000
G1 X-20.000
G1 Y-20.000
G90
M2
