using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace WG16.Lab;

/// Loads the lab control registry + material data from JSON (decomposition follow-up). Pure data — no Godot
/// scene tree, no widgets — so it lifts cleanly out of TerrainLabUI's panel partial. Parses
/// data/lab_controls.json into LabControl objects (zone/companion-expanded), the material library, and the
/// ground palette. The panel builder (BuildPanel/BuildRow) stays on TerrainLabUI; this only produces the data
/// it consumes.
public static class LabRegistryLoader
{
    /// data/material_library.json -> sorted material name list (appends into `materials`).
    public static void LoadLibrary(List<string> materials)
    {
        string abs = ProjectSettings.GlobalizePath("res://data/material_library.json");
        if (System.IO.File.Exists(abs))
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
            foreach (JsonElement m in doc.RootElement.GetProperty("materials").EnumerateArray())
            {
                materials.Add(m.GetProperty("name").GetString() ?? "");
            }
        }
        materials.Sort();
    }

    /// GM1: data/ground_palette.json active palette -> role->material names, or null (then
    /// ZoneDefaultMaterialIndex uses its hardcoded fallback). Malformed JSON must NOT crash startup.
    public static string[]? LoadGroundPalette()
    {
        string abs = ProjectSettings.GlobalizePath("res://data/ground_palette.json");
        if (!System.IO.File.Exists(abs)) { return null; }
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
            JsonElement root = doc.RootElement;
            string active = root.GetProperty("active").GetString() ?? "";
            if (root.GetProperty("palettes").TryGetProperty(active, out var pal)
                && pal.TryGetProperty("roles", out var roles))
            {
                var palette = roles.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
                GD.Print($"[ground_palette] active '{active}' loaded ({palette.Length} roles)");
                return palette;
            }
            GD.PushWarning($"[ground_palette] active '{active}' not found → using fallback");
            return null;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[ground_palette] parse failed ({e.Message}) → using hardcoded fallback");
            return null;
        }
    }

    /// data/lab_controls.json -> populates `controls` (flat, zone/companion-expanded) + `byId`
    /// (keyed by id, or "id#z" for zone-expanded entries). Returns the zone names.
    public static void LoadRegistry(string registryPath, List<LabControl> controls,
                                    Dictionary<string, LabControl> byId, out string[] zoneNames)
    {
        string abs = ProjectSettings.GlobalizePath(registryPath);
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        JsonElement root = doc.RootElement;
        // zone_names was removed in the 2026-06-21 ground strip; tolerate its absence.
        zoneNames = root.TryGetProperty("zone_names", out var zn)
            ? zn.EnumerateArray().Select(e => e.GetString() ?? "").ToArray()
            : System.Array.Empty<string>();

        foreach (JsonElement c in root.GetProperty("controls").EnumerateArray())
        {
            string type = c.GetProperty("type").GetString() ?? "";
            if (type == "material" || type == "companion")
            {
                int[] defs = c.TryGetProperty("default", out var dArr)
                    ? dArr.EnumerateArray().Select(e => e.GetInt32()).ToArray() : null;
                for (int z = 0; z < zoneNames.Length; z++)
                {
                    var lc = BaseControl(c, type);
                    lc.Zone = z;
                    lc.Label = zoneNames[z];
                    if (type == "companion") { lc.Default = defs != null ? defs[z] : Math.Max(0, z - 1); }
                    Register(controls, byId, lc, $"{lc.Id}#{z}");
                }
            }
            else
            {
                var lc = BaseControl(c, type);
                Register(controls, byId, lc, lc.Id);
            }
        }
    }

    private static LabControl BaseControl(JsonElement c, string type)
    {
        var lc = new LabControl
        {
            Id = c.GetProperty("id").GetString() ?? "",
            Label = c.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
            Tab = c.GetProperty("tab").GetString() ?? "",
            Type = type,
            Param = c.TryGetProperty("param", out var p) ? p.GetString() : null,
            Setter = c.TryGetProperty("setter", out var s) ? s.GetString() : null,
            Field = c.TryGetProperty("field", out var f) ? f.GetString() : null,
            Scene = c.TryGetProperty("scene", out var sc) ? sc.GetString() : null,
            Cloud = c.TryGetProperty("cloud", out var cl) ? cl.GetString() : null,
            Rand = !c.TryGetProperty("rand", out var r) || r.GetBoolean(),
            Rebake = c.TryGetProperty("rebake", out var rb) && rb.GetBoolean(),
        };
        if (c.TryGetProperty("min", out var mn)) { lc.Min = mn.GetSingle(); }
        if (c.TryGetProperty("max", out var mx)) { lc.Max = mx.GetSingle(); }
        // U2: objectlist fields (item schema + data array + bounds). Harmless for other types.
        lc.ItemSchema = c.TryGetProperty("item_schema", out var isc) ? isc.GetString() : null;
        lc.DataPath = c.TryGetProperty("data", out var dp) ? dp.GetString() : null;
        if (c.TryGetProperty("min_items", out var mi)) { lc.MinItems = mi.GetInt32(); }
        if (c.TryGetProperty("max_items", out var ma)) { lc.MaxItems = ma.GetInt32(); }
        if (c.TryGetProperty("options", out var op)) { lc.Options = op.EnumerateArray().Select(e => e.GetString() ?? "").ToArray(); }
        if (c.TryGetProperty("default", out var d) && d.ValueKind != JsonValueKind.Array)
        {
            if (type == "toggle" || type == "scene" || type == "cloud") { lc.DefBool = d.GetBoolean(); }
            else { lc.Default = d.GetSingle(); }
        }
        if (type == "scenecolor" && c.TryGetProperty("default", out var dc) && dc.ValueKind == JsonValueKind.Array)
        {
            var a = dc.EnumerateArray().Select(e => e.GetSingle()).ToArray();
            if (a.Length >= 3) { lc.DefColor = new Color(a[0], a[1], a[2]); }
        }
        return lc;
    }

    private static void Register(List<LabControl> controls, Dictionary<string, LabControl> byId, LabControl lc, string key)
    {
        controls.Add(lc);
        byId[key] = lc;
    }

    /// GM1: role->default material index, with a hardcoded fallback contrast set + a clamped last resort.
    /// A miss WARNS so a bad palette name is visible, not silently arbitrary.
    public static int ZoneDefaultMaterialIndex(string[]? groundPalette, List<string> materials, int zone)
    {
        string[] fallback = { "m8_grass_calm", "dirt", "16_glacial_till",
                              "02_coarse_talus", "rock_dark", "m14_tundra_moss", "01_fresh_powder" };
        string want = (groundPalette != null && zone >= 0 && zone < groundPalette.Length)
                      ? groundPalette[zone]
                      : (zone >= 0 && zone < fallback.Length ? fallback[zone] : "");
        int idx = materials.IndexOf(want);
        if (idx < 0)
        {
            GD.PushWarning($"[ground_palette] role {zone} material '{want}' not in library → clamped fallback");
            idx = Math.Min(Math.Max(zone, 0), materials.Count - 1);
        }
        return Math.Max(idx, 0);
    }
}
