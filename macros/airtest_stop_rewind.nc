(Air-cut test program - for exercising Feed Hold / Stop / Rewind without cutting.)
(SAFETY: this file contains NO Z WORD and NO SPINDLE COMMAND, so it cannot plunge)
(and cannot start the spindle. Jog to a safe height and a clear area BEFORE running.)
(Motion is G91 INCREMENTAL and every square returns to its own start point, so the)
(machine never travels more than 20mm from where you began, however long it runs.)
(10 squares at F1500 = about 32 seconds. Each square prints its number, which)
(grblHAL echoes as [MSG:...] - if you see no messages, enable Settings > SendComments.)
(Soft limits still apply: starting within 20mm of a limit alarms rather than moving.)
G21 G94 G17
G91
(PRINT, Square 1 of 10)
G1 X20.000 F1500
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(PRINT, Square 2 of 10)
G1 X20.000 F1500
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(PRINT, Square 3 of 10)
G1 X20.000 F1500
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(PRINT, Square 4 of 10)
G1 X20.000 F1500
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(PRINT, Square 5 of 10)
G1 X20.000 F1500
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(PRINT, Square 6 of 10)
G1 X20.000 F1500
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(PRINT, Square 7 of 10)
G1 X20.000 F1500
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(PRINT, Square 8 of 10)
G1 X20.000 F1500
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(PRINT, Square 9 of 10)
G1 X20.000 F1500
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(PRINT, Square 10 of 10)
G1 X20.000 F1500
G1 Y20.000
G1 X-20.000
G1 Y-20.000
(PRINT, Air test complete)
G90
M2
