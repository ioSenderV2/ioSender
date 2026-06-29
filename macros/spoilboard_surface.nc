(ioSender spoilboard surfacing - 860.000 x 860.000 mm area, 25.000 mm bit, 40% overlap, outline only)
(stepover 15.000 mm, 1 depth pass(es), DOC 0.300 mm to 0.300 mm total, spindle 15000 rpm)
(Jog to the front-left corner, touch the bit to the surface and zero work XYZ there - Z0 = surface top.)
G90 G94
G17
G21
G53 G0 Z0
S15000 M3
G17 G90 G94
G54
G0 Z5.000
(depth pass 1 of 1 at Z-0.300)
G0 X0.000 Y0.000
G1 Z-0.300 F100.000
G1 X860.000 Y0.000 F300.000
G1 X860.000 Y860.000 F300.000
G1 X0.000 Y860.000 F300.000
G1 X0.000 Y0.000 F300.000
G0 Z5.000
M5
G53 G0 Z0
M30
