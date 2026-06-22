using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace WG16.Lab;

/// One field of an object-list item's schema. Mirrors the flat-registry control types
/// (float/slider, bool, color, enum) so the SAME widget builders render it. Pure data.
public sealed class SchemaField
{
    public string Id = "", Type = "", Label = "";
    public float Min, Max, Default;                   // float/slider
    public bool DefBool;                              // bool
    public Color DefColor = Colors.White;             // color ([r,g,b])
    public string[] Options = Array.Empty<string>();  // enum (Default = selected index)
}

/// Loads res://data/item_schemas.json: { "<name>": [ {field}, ... ], ... }. Schema names are
/// referenced by an `objectlist` registry entry's `item_schema`. The list editor reuses the
/// flat-registry widget builders per field type, so a schema is just a list of typed fields.
public static class ItemSchemas
{
    public static Dictionary<string, List<SchemaField>> Load()
    {
        var result = new Dictionary<string, List<SchemaField>>();
        string abs = ProjectSettings.GlobalizePath("res://data/item_schemas.json");
        if (!System.IO.File.Exists(abs)) { return result; }
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        foreach (JsonProperty schema in doc.RootElement.EnumerateObject())
        {
            var fields = new List<SchemaField>();
            foreach (JsonElement fe in schema.Value.EnumerateArray())
            {
                var f = new SchemaField
                {
                    Id = fe.GetProperty("id").GetString() ?? "",
                    Type = fe.GetProperty("type").GetString() ?? "",
                    Label = fe.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                };
                if (fe.TryGetProperty("min", out var mn)) { f.Min = mn.GetSingle(); }
                if (fe.TryGetProperty("max", out var mx)) { f.Max = mx.GetSingle(); }
                if (fe.TryGetProperty("options", out var op)) { f.Options = op.EnumerateArray().Select(e => e.GetString() ?? "").ToArray(); }
                if (fe.TryGetProperty("default", out var d))
                {
                    if (f.Type == "bool") { f.DefBool = d.GetBoolean(); }
                    else if (f.Type == "color" && d.ValueKind == JsonValueKind.Array)
                    {
                        var a = d.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                        if (a.Length >= 3) { f.DefColor = new Color(a[0], a[1], a[2]); }
                    }
                    else { f.Default = d.GetSingle(); }   // float OR enum index
                }
                fields.Add(f);
            }
            result[schema.Name] = fields;
        }
        return result;
    }
}
