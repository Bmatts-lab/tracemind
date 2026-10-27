using TraceMind.Core.Geometry;
using TraceMind.Core.Library;
using TraceMind.Core.Model;

namespace TraceMind.Core.Samples;

public static class SampleProjects
{
    /// <summary>USB-powered ATtiny85 board: micro-USB → AMS1117-3.3 → ATtiny85 with two LEDs, a button and an ISP header.</summary>
    public static Project AttinyBlinker()
    {
        var p = new Project { Name = "ATtiny85 USB blinker" };
        p.Board.Outline = [new(0, 0), new(45, 0), new(45, 30), new(0, 30)];
        p.Board.Rules.NetClasses.Add(new NetClass { Name = "Power", TrackWidth = 0.5, Clearance = 0.2, Nets = ["VBUS", "+3V3", "GND"] });

        var mcuSym = SymbolLibrary.BuildIc("ATtiny85", "ATtiny85-20S",
        [
            new("1", "PB5/~RESET", PinType.Bidirectional), new("2", "PB3", PinType.Bidirectional), new("3", "PB4", PinType.Bidirectional),
            new("4", "GND", PinType.PowerIn), new("5", "PB0/MOSI", PinType.Bidirectional), new("6", "PB1/MISO", PinType.Bidirectional),
            new("7", "PB2/SCK", PinType.Bidirectional), new("8", "VCC", PinType.PowerIn),
        ]);
        p.Symbols[mcuSym.Id] = mcuSym;
        var ldoSym = SymbolLibrary.BuildIc("AMS1117-3.3", "AMS1117-3.3",
        [
            new("3", "VIN", PinType.PowerIn, PinSide.Left), new("2", "VOUT", PinType.PowerOut, PinSide.Right), new("1", "GND", PinType.PowerIn, PinSide.Bottom),
        ]);
        p.Symbols[ldoSym.Id] = ldoSym;
        var usbSym = SymbolLibrary.BuildIc("USB_Micro_B", "USB Micro-B",
        [
            new("1", "VBUS", PinType.PowerOut, PinSide.Right), new("2", "D-", PinType.Bidirectional, PinSide.Right), new("3", "D+", PinType.Bidirectional, PinSide.Right),
            new("4", "ID", PinType.Passive, PinSide.Right), new("5", "GND", PinType.PowerOut, PinSide.Bottom), new("6", "SHIELD", PinType.Passive, PinSide.Bottom),
        ], "J");
        p.Symbols[usbSym.Id] = usbSym;

        var u1 = EditOps.AddComponent(p, "ATtiny85", "ATtiny85-20S", "SOIC-8", "U1");
        u1.Mpn = "ATTINY85-20SU"; u1.Manufacturer = "Microchip"; u1.SupplierPart = "C89852"; u1.Datasheet = "https://ww1.microchip.com/downloads/en/DeviceDoc/Atmel-2586-AVR-8-bit-Microcontroller-ATtiny25-ATtiny45-ATtiny85_Datasheet.pdf";
        var u2 = EditOps.AddComponent(p, "AMS1117-3.3", "AMS1117-3.3", "SOT-223", "U2");
        u2.Mpn = "AMS1117-3.3"; u2.Manufacturer = "Advanced Monolithic Systems"; u2.SupplierPart = "C6186";
        var j1 = EditOps.AddComponent(p, "USB_Micro_B", "USB_Micro_B", "USB_Micro_B", "J1");
        var c1 = EditOps.AddComponent(p, "C", "10uF", "C_0805", "C1"); c1.SupplierPart = "C15850";
        var c2 = EditOps.AddComponent(p, "C", "10uF", "C_0805", "C2"); c2.SupplierPart = "C15850";
        var c3 = EditOps.AddComponent(p, "C", "100nF", "C_0603", "C3"); c3.SupplierPart = "C14663";
        var r1 = EditOps.AddComponent(p, "R", "330", "R_0603", "R1"); r1.SupplierPart = "C23138";
        var r2 = EditOps.AddComponent(p, "R", "330", "R_0603", "R2"); r2.SupplierPart = "C23138";
        var r3 = EditOps.AddComponent(p, "R", "10k", "R_0603", "R3"); r3.SupplierPart = "C25804";
        var d1 = EditOps.AddComponent(p, "LED", "Red", "LED_0603", "D1"); d1.SupplierPart = "C2286";
        var d2 = EditOps.AddComponent(p, "LED", "Green", "LED_0603", "D2"); d2.SupplierPart = "C72043";
        var sw1 = EditOps.AddComponent(p, "SW", "Reset", "SW_SMD_4x4", "SW1");
        var j2 = EditOps.AddComponent(p, "Conn_2x03", "ISP", "PinHeader_2x03_P2.54", "J2");

        EditOps.ConnectPins(p, "VBUS", [("J1", "1"), ("U2", "3"), ("C1", "1")]);
        EditOps.ConnectPins(p, "GND", [("J1", "5"), ("J1", "6"), ("U2", "1"), ("C1", "2"), ("C2", "2"), ("C3", "2"), ("U1", "4"), ("D1", "1"), ("D2", "1"), ("SW1", "2"), ("J2", "6")]);
        EditOps.ConnectPins(p, "+3V3", [("U2", "2"), ("C2", "1"), ("C3", "1"), ("U1", "8"), ("R3", "1"), ("J2", "2")]);
        EditOps.ConnectPins(p, "RESET", [("U1", "1"), ("R3", "2"), ("SW1", "1"), ("J2", "5")]);
        EditOps.ConnectPins(p, "MOSI", [("U1", "5"), ("J2", "4")]);
        EditOps.ConnectPins(p, "MISO", [("U1", "6"), ("J2", "1")]);
        EditOps.ConnectPins(p, "SCK", [("U1", "7"), ("J2", "3")]);
        EditOps.ConnectPins(p, "LED_R", [("U1", "3"), ("R1", "1")]);
        EditOps.ConnectPins(p, "LED_G", [("U1", "2"), ("R2", "1")]);
        EditOps.ConnectPins(p, "Net-D1", [("R1", "2"), ("D1", "2")]);
        EditOps.ConnectPins(p, "Net-D2", [("R2", "2"), ("D2", "2")]);
        EditOps.MarkNoConnect(p, "J1", "2"); EditOps.MarkNoConnect(p, "J1", "3"); EditOps.MarkNoConnect(p, "J1", "4");
        EditOps.TidySchematic(p);

        // A sensible manual placement (connector on the left edge).
        void Place(Component c, double x, double y, double rot = 0) { c.PcbPos = new(x, y); c.PcbRot = rot; c.Placed = true; }
        Place(j1, 4.5, 15, 270);
        Place(u2, 14, 15);
        Place(c1, 9, 22, 90); Place(c2, 19.5, 22, 90); Place(c3, 27, 9.5, 0);
        Place(u1, 27, 15);
        Place(r1, 34, 11, 0); Place(r2, 34, 19, 0);
        Place(d1, 39.5, 11, 0); Place(d2, 39.5, 19, 0);
        Place(r3, 27, 22.5, 0); Place(sw1, 34, 25.5, 0);
        Place(j2, 15, 6, 90);
        p.Board.Texts.Add(new BoardText { Text = "TraceMind", Position = new(36, 3), Size = 1.2 });
        p.Board.Holes.Add(new MountingHole { Position = new(3, 3), Diameter = 2.2 });
        p.Board.Holes.Add(new MountingHole { Position = new(42, 27), Diameter = 2.2 });
        return p;
    }
}
