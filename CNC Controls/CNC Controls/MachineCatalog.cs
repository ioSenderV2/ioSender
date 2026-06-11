/*
 * MachineCatalog.cs - part of CNC Controls library
 *
 * Hierarchical machine catalog (Manufacturer -> Product -> Model) that seeds the Machine Setup Wizard.
 * Values are STARTING POINTS the user confirms - working areas from vendor pages, steps/mm from the
 * published value or the drive (belt 20T GT2 ~40/mm; T8 leadscrew ~800/mm; else left 0 = "leave current"),
 * max rate published-or-default. Travel arrays are the stored $130-$132 (the wizard adds back pull-off).
 * A model with Grbl == false (e.g. Onefinity) is listed for completeness but cannot be seeded.
 *
 */

using System.Collections.Generic;

namespace CNC.Controls
{
    // Leaf: one selectable machine. Arrays are X,Y,Z; an element of 0 (steps) or a null array = "leave as is".
    public class MachineModel
    {
        public string Name { get; set; }
        public bool Grbl { get; set; } = true;
        public double[] StepsPerMm { get; set; }   // $100-$102 (0 in an element = skip that axis)
        public double[] MaxRate { get; set; }      // $110-$112 (mm/min)
        public double[] Travel { get; set; }       // $130-$132 (stored, mm)
        public int HomingDirMask { get; set; } = -1;   // $23 suggestion; -1 = leave
        public bool? Homing { get; set; }              // $22 enable suggestion
        public string Note { get; set; }
        public override string ToString() { return Name; }
    }

    public class MachineProduct
    {
        public string Name { get; set; }
        public List<MachineModel> Models { get; set; } = new List<MachineModel>();
        public override string ToString() { return Name; }
    }

    public class MachineManufacturer
    {
        public string Name { get; set; }
        public List<MachineProduct> Products { get; set; } = new List<MachineProduct>();
        public override string ToString() { return Name; }
    }

    public static class MachineCatalog
    {
        // Small helpers to keep the data terse.
        private static double[] A(double x, double y, double z) { return new[] { x, y, z }; }
        private static MachineModel M(string name, double[] travel, double[] steps = null, double[] rate = null,
                                      int homingDir = -1, bool? homing = null, bool grbl = true, string note = null)
        {
            return new MachineModel { Name = name, Travel = travel, StepsPerMm = steps, MaxRate = rate,
                                      HomingDirMask = homingDir, Homing = homing, Grbl = grbl, Note = note };
        }
        private static MachineProduct P(string name, params MachineModel[] models)
        {
            return new MachineProduct { Name = name, Models = new List<MachineModel>(models) };
        }

        private const string SHP = "Belt X/Y (~40 steps/mm); confirm Z steps/mm and max rate for your unit.";
        private const string LM  = "Sienci-published defaults (original LongBoard; MK2.5/SuperLongBoard differ).";
        private const string XC  = "Pre-2021; the 2021 9mm-belt / extended-Z kit changes $100/$101/$102/$132.";
        private const string ONEFINITY = "Onefinity uses Buildbotics / Masso / Redline - not grbl. Listed for reference; this wizard configures grbl settings only.";
        private const string VERIFY = "Confirm steps/mm and travel against your machine - drive-dependent.";

        public static List<MachineManufacturer> Manufacturers
        {
            get
            {
                return new List<MachineManufacturer>
                {
                    new MachineManufacturer { Name = "Generic / custom", Products = new List<MachineProduct>
                    {
                        P("3-axis CNC",
                            M("With limit switches (homing)", null, homing: true,
                              note: "Generic 3-axis machine with limit switches - homing and limits on. Enter your travel and steps/mm in the axis table."),
                            M("Without limit switches", null, homing: false,
                              note: "Generic 3-axis machine, no limit switches - homing off. Enter your travel and steps/mm in the axis table."),
                            M("Custom (enter everything manually)", null,
                              note: "Nothing is pre-filled - set every value yourself."))
                    }},

                    new MachineManufacturer { Name = "Carbide3D", Products = new List<MachineProduct>
                    {
                        P("Shapeoko",
                            M("Shapeoko Pro — Standard (16×16)", A(406,406,100), A(40,40,0), note: SHP),
                            M("Shapeoko Pro — XL (16×33)",       A(406,838,100), A(40,40,0), note: SHP),
                            M("Shapeoko Pro — XXL (33×33)",      A(838,838,100), A(40,40,0), note: SHP),
                            M("Shapeoko 4 — Standard (16×16)",   A(406,406,100), A(40,40,0), note: SHP),
                            M("Shapeoko 4 — XL (16×33)",         A(406,838,100), A(40,40,0), note: SHP),
                            M("Shapeoko 4 — XXL (33×33)",        A(838,838,100), A(40,40,0), note: SHP),
                            M("Shapeoko 5 Pro — 2×2",            A(610,610,140), A(40,40,0), note: SHP),
                            M("Shapeoko 5 Pro — 4×2",            A(1219,610,140), A(40,40,0), note: SHP),
                            M("Shapeoko 5 Pro — 4×4",            A(1219,1219,140), A(40,40,0), note: SHP),
                            M("Shapeoko HDM (27×21)",            A(686,533,140), A(40,40,0), note: SHP),
                            M("Shapeoko 3 — XXL (legacy)",       A(838,838,95), A(40,40,320), note: SHP)),
                        P("Nomad",
                            M("Nomad 3", A(203,203,76), note: "Enclosed desktop mill, leadscrew - " + VERIFY))
                    }},

                    new MachineManufacturer { Name = "Inventables", Products = new List<MachineProduct>
                    {
                        P("X-Carve",
                            M("X-Carve 1000mm", A(750,750,100), A(40,40,188.95), A(8000,8000,500), note: XC),
                            M("X-Carve 750mm",  A(535,535,100), A(40,40,188.95), A(8000,8000,500), note: XC),
                            M("X-Carve 500mm",  A(290,290,100), A(40,40,188.95), A(8000,8000,500), note: XC)),
                        P("X-Carve Pro",
                            M("X-Carve Pro 4×2", A(1219,610,120), note: "25mm ballscrew, closed-loop. " + VERIFY),
                            M("X-Carve Pro 4×4", A(1219,1219,120), note: "25mm ballscrew, closed-loop. " + VERIFY)),
                        P("Carvey",
                            M("Carvey (discontinued)", A(300,200,70), note: "Enclosed desktop - " + VERIFY))
                    }},

                    new MachineManufacturer { Name = "MillRight CNC", Products = new List<MachineProduct>
                    {
                        P("Mega V",
                            M("Mega V 2 (19×19)",      A(483,483,152), note: "Leadscrew; grbl or Masso option. " + VERIFY),
                            M("Mega V 2 XL (35×35)",   A(889,889,152), note: VERIFY),
                            M("Mega V 2 Full Sheet",   A(1219,2438,152), note: VERIFY),
                            M("Mega V Pro (25×25)",    A(635,635,140), note: VERIFY)),
                        P("Carve King",
                            M("Carve King 2 (17×17)",  A(432,432,102), note: "Uno + grbl, leadscrew. " + VERIFY),
                            M("Carve King (original)", A(255,255,80), note: "Discontinued. " + VERIFY)),
                        P("Power Route",
                            M("Power Route Plus", A(635,635,100), note: "Confirm work area + steps/mm."))
                    }},

                    new MachineManufacturer { Name = "Sienci Labs", Products = new List<MachineProduct>
                    {
                        P("LongMill MK2 / MK2.5",
                            M("LongMill 30×30", A(810,855,120), A(200,200,200), A(4000,4000,3000), 3, false, note: LM),
                            M("LongMill 30×12", A(810,355,120), A(200,200,200), A(4000,4000,3000), 3, false, note: LM),
                            M("LongMill 48×30", A(1270,855,120), A(200,200,200), A(4000,4000,3000), 3, false, note: LM)),
                        P("AltMill (grblHAL)",
                            M("AltMill Mark 2 — 2×4", A(610,1219,120), note: VERIFY),
                            M("AltMill Mark 2 — 4×4", A(1219,1219,120), note: VERIFY),
                            M("AltMill — 4×8",        A(1219,2438,120), note: VERIFY))
                    }},

                    new MachineManufacturer { Name = "BobsCNC", Products = new List<MachineProduct>
                    {
                        P("Evolution",
                            M("Evolution 4 (E4)", A(610,610,85), A(80,80,400), homing: false, note: "Ships without limit switches - no homing."),
                            M("Evolution 3 (E3)", A(457,390,85), A(80,80,400), homing: false, note: "Ships without limit switches - no homing."))
                    }},

                    new MachineManufacturer { Name = "Generic / DIY", Products = new List<MachineProduct>
                    {
                        P("CNC 3018",
                            M("CNC 3018 / 3018-PRO", A(299,179,44), A(800,800,800), A(1000,1000,800),
                              note: "Steps/mm vary by board/microstepping (also 400 or 1600 seen) - verify."),
                            M("Genmitsu 3018-PROVer", A(299,179,44), A(800,800,800), A(1000,1000,800), homing: true,
                              note: "Has limit switches. " + "Steps/mm vary by microstepping - verify.")),
                        P("CNC 4030 / 4040",
                            M("Genmitsu PROVerXL 4030", A(400,300,110), note: VERIFY),
                            M("Genmitsu PROVerXL 4040", A(400,400,110), note: VERIFY))
                    }},

                    new MachineManufacturer { Name = "Onefinity (not grbl)", Products = new List<MachineProduct>
                    {
                        P("Original (Buildbotics)",
                            M("Woodworker (X-35) 32×32", A(813,813,133), grbl: false, note: ONEFINITY),
                            M("Journeyman (X-50) 32×48", A(813,1219,133), grbl: false, note: ONEFINITY),
                            M("Foreman 48×48",           A(1219,1219,133), grbl: false, note: ONEFINITY)),
                        P("Elite (Masso / Redline)",
                            M("Elite Woodworker 32×32",  A(813,813,133), grbl: false, note: ONEFINITY),
                            M("Elite Journeyman 48×32",  A(1219,813,133), grbl: false, note: ONEFINITY),
                            M("Elite Foreman 48×48",     A(1219,1219,133), grbl: false, note: ONEFINITY))
                    }}
                };
            }
        }
    }
}
