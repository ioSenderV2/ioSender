(Air-cut test program - for exercising Feed Hold / Stop / Rewind without cutting.)
(SAFETY: this file contains NO Z WORD and NO SPINDLE COMMAND, so it cannot plunge)
(and cannot start the spindle. Jog to a safe height and a clear area BEFORE running.)
(Motion is G91 INCREMENTAL and every square returns to its own start point, so the)
(machine never travels more than 20mm from where you began, however long it runs.)
(10 squares at F1500 = about 32 seconds. Each square prints its number, which)
(grblHAL echoes as [MSG:...] - if you see no messages, enable Settings > SendComments.)
(Soft limits still apply: starting within 20mm of a limit alarms rather than moving.)
N10 G21 G94 G17
N20 G91
(PRINT, Square 1 of 10)
N30 G1 X20.000 F1500
N40 G1 Y20.000
N50 G1 X-20.000
N60 G1 Y-20.000
(PRINT, Square 2 of 10)
N70 G1 X20.000 F1500
N80 G1 Y20.000
N90 G1 X-20.000
N100 G1 Y-20.000
(PRINT, Square 3 of 10)
N110 G1 X20.000 F1500
N120 G1 Y20.000
N130 G1 X-20.000
N140 G1 Y-20.000
(PRINT, Square 4 of 10)
N150 G1 X20.000 F1500
N160 G1 Y20.000
N170 G1 X-20.000
N180 G1 Y-20.000
(PRINT, Square 5 of 10)
N190 G1 X20.000 F1500
N200 G1 Y20.000
N210 G1 X-20.000
N220 G1 Y-20.000
(PRINT, Square 6 of 10)
N230 G1 X20.000 F1500
N240 G1 Y20.000
N250 G1 X-20.000
N260 G1 Y-20.000
(PRINT, Square 7 of 10)
N270 G1 X20.000 F1500
N280 G1 Y20.000
N290 G1 X-20.000
N300 G1 Y-20.000
(PRINT, Square 8 of 10)
N310 G1 X20.000 F1500
N320 G1 Y20.000
N330 G1 X-20.000
N340 G1 Y-20.000
(PRINT, Square 9 of 10)
N350 G1 X20.000 F1500
N360 G1 Y20.000
N370 G1 X-20.000
N380 G1 Y-20.000
(PRINT, Square 10 of 10)
N390 G1 X20.000 F1500
N400 G1 Y20.000
N410 G1 X-20.000
N420 G1 Y-20.000
(PRINT, Air test complete)
N430 G90
N440 M2
