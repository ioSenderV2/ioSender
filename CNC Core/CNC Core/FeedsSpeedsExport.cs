/*
 * FeedsSpeedsExport.cs - part of CNC Core library
 *
 * POCO deserialization target for the JSON the ioSenderV2 Fusion add-in's "Feeds and
 * Speeds" command writes to ~/Downloads/ioSenderV2/<docName>.json (feedsAndSpeeds.py's
 * _build_payload). Plain properties only - this is a one-shot read of an externally
 * produced file, not an editable in-app settings list, so it skips the
 * INotifyPropertyChanged/XmlSerializer ceremony ProbeDefinition.cs/Fixture.cs use.
 *
 */

using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CNC.Core
{
    public class FeedsSpeedsExport
    {
        public string Document { get; set; }

        [JsonPropertyName("op_count")]
        public int OpCount { get; set; }

        public List<FeedsSpeedsSetup> Setups { get; set; } = new List<FeedsSpeedsSetup>();
    }

    public class FeedsSpeedsSetup
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public List<FeedsSpeedsOperation> Operations { get; set; } = new List<FeedsSpeedsOperation>();
    }

    public class FeedsSpeedsOperation
    {
        public string Id { get; set; }

        [JsonPropertyName("setup_index")]
        public int SetupIndex { get; set; }

        [JsonPropertyName("op_index")]
        public int OpIndex { get; set; }

        public string Name { get; set; }
        public string Strategy { get; set; }
        public FeedsSpeedsTool Tool { get; set; }
        public FeedsSpeedsCurrent Current { get; set; }

        // Hole/depth/stock geometry (feedsAndSpeeds.py's _geometry_info) - shape varies per
        // strategy, and nothing in the decision engine needs it parsed out yet, so it's kept
        // as a raw element rather than a matching POCO tree.
        public JsonElement? Geometry { get; set; }

        // Set instead of the fields above when this op's extraction raised in Fusion
        // (feedsAndSpeeds.py's _build_payload catches per-op, not per-document).
        public string Error { get; set; }
    }

    public class FeedsSpeedsTool
    {
        public string Name { get; set; }
        public string Type { get; set; }

        [JsonPropertyName("diameter_mm")]
        public double? DiameterMm { get; set; }

        public double? Flutes { get; set; }
    }

    public class FeedsSpeedsCurrent
    {
        public double? Rpm { get; set; }

        [JsonPropertyName("cutting_feed")]
        public double? CuttingFeed { get; set; }

        [JsonPropertyName("plunge_feed")]
        public double? PlungeFeed { get; set; }

        [JsonPropertyName("axial_step")]
        public double? AxialStep { get; set; }

        [JsonPropertyName("radial_step")]
        public double? RadialStep { get; set; }

        public string Coolant { get; set; }
    }

    // Root of the <docName>-apply.json file this app writes and the Fusion add-in's Apply
    // action reads back (feedsAndSpeeds.py's apply_from_file / _APPLY_UNITS keys).
    public class FeedsSpeedsApplyFile
    {
        public List<FeedsSpeedsApplyOp> Ops { get; set; } = new List<FeedsSpeedsApplyOp>();
    }

    public class FeedsSpeedsApplyOp
    {
        public string Id { get; set; }

        // Keys are exactly _APPLY_UNITS' keys on the Fusion side: rpm, cutting_feed,
        // plunge_feed, axial_step, radial_step.
        public Dictionary<string, double> Set { get; set; } = new Dictionary<string, double>();
    }
}
