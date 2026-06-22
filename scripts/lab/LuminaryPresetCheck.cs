using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// U3 deterministic round-trip self-check (--lumpresetcheck): proves a luminary set survives the FULL
/// preset serialization path — dict -> Godot Json string (exactly how WritePresets stores it) -> parsed
/// back -> dicts -> Luminary — with every field (incl. Color, the flagged risk) intact. Pure logic, no
/// RenderingDevice, so it runs headless and prints LUMPRESETCHECK: PASS/FAIL. The save/load wiring in
/// TerrainLabUI.UserPresets.cs uses these same DictFromLuminary/LuminaryFromDict converters.
public static class LuminaryPresetCheck
{
    public static bool Run(
        System.Func<Luminary, Godot.Collections.Dictionary> toDict,
        System.Func<Godot.Collections.Dictionary, Luminary> fromDict,
        System.Func<Godot.Collections.Dictionary, Godot.Collections.Dictionary> toStorable,
        System.Func<Godot.Collections.Dictionary, Godot.Collections.Dictionary> fromStorable,
        out string msg)
    {
        // A representative edited set: 2 suns (one companion) + 2 moons, distinct in every field.
        var original = new List<Luminary>
        {
            new() { Kind = LuminaryKind.Sun,  Color = new Color(1.0f, 0.95f, 0.86f), Size = 0.6f, Phase = 1.0f, AzOffset = 0f,   DeclScale = 1.0f,  LightEnergy = 1.3f, CastsShadow = true,  ContributesToAtmosphere = true,  Priority = 100f },
            new() { Kind = LuminaryKind.Sun,  Color = new Color(0.62f, 0.78f, 1.0f), Size = 0.9f, Phase = 0.5f, AzOffset = 60f,  DeclScale = 0.88f, LightEnergy = 1.1f, CastsShadow = false, ContributesToAtmosphere = true,  Priority = 80f  },
            new() { Kind = LuminaryKind.Moon, Color = new Color(0.85f, 0.88f, 1.0f), Size = 1.2f, Phase = 1.0f, AzOffset = 35f,  DeclScale = 0.72f, LightEnergy = 0.5f, CastsShadow = true,  ContributesToAtmosphere = false, Priority = 10f  },
            new() { Kind = LuminaryKind.Moon, Color = new Color(1.0f, 0.82f, 0.86f), Size = 0.8f, Phase = 0.85f,AzOffset = -40f, DeclScale = 0.6f,  LightEnergy = 0.3f, CastsShadow = false, ContributesToAtmosphere = false, Priority = 5f   },
        };

        // SAVE path (== SavePreset): Luminary -> dict -> storable (Color->[r,g,b]) -> Array -> Json string.
        var arr = new Godot.Collections.Array();
        foreach (var b in original) { arr.Add(toStorable(toDict(b))); }
        string json = Json.Stringify(arr);

        // LOAD path (== LoadSelectedPreset): parse -> array -> from-storable ([r,g,b]->Color) -> Luminary.
        Variant parsed = Json.ParseString(json);
        if (parsed.VariantType != Variant.Type.Array) { msg = "parsed JSON is not an array"; return false; }
        var loaded = new List<Luminary>();
        foreach (var v in parsed.AsGodotArray()) { loaded.Add(fromDict(fromStorable(v.AsGodotDictionary()))); }

        bool ok = true;
        var fails = new List<string>();
        void Check(bool cond, string what) { if (!cond) { ok = false; fails.Add(what); } }

        Check(loaded.Count == original.Count, $"count {loaded.Count}=={original.Count}");
        for (int i = 0; i < System.Math.Min(loaded.Count, original.Count); i++)
        {
            var a = original[i]; var b = loaded[i];
            Check(a.Kind == b.Kind, $"[{i}] kind");
            Check(ColorEq(a.Color, b.Color), $"[{i}] color {a.Color}!={b.Color}");
            Check(Mathf.IsEqualApprox(a.Size, b.Size), $"[{i}] size");
            Check(Mathf.IsEqualApprox(a.Phase, b.Phase), $"[{i}] phase");
            Check(Mathf.IsEqualApprox(a.AzOffset, b.AzOffset), $"[{i}] az");
            Check(Mathf.IsEqualApprox(a.DeclScale, b.DeclScale), $"[{i}] decl");
            Check(Mathf.IsEqualApprox(a.LightEnergy, b.LightEnergy), $"[{i}] energy");
            Check(a.CastsShadow == b.CastsShadow, $"[{i}] shadow");
            Check(a.ContributesToAtmosphere == b.ContributesToAtmosphere, $"[{i}] atmosphere");
            Check(Mathf.IsEqualApprox(a.Priority, b.Priority), $"[{i}] priority");
        }

        msg = ok ? $"4 bodies round-tripped through Json intact (all fields, incl. Color)" : string.Join("; ", fails);
        return ok;
    }

    private static bool ColorEq(Color a, Color b) =>
        Mathf.IsEqualApprox(a.R, b.R) && Mathf.IsEqualApprox(a.G, b.G) && Mathf.IsEqualApprox(a.B, b.B);
}
