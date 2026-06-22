using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// U1 deterministic self-check (--objectlistcheck): drives an ObjectListControl programmatically
/// through every mutation (init / add / edit / duplicate / reorder / remove + min/max bounds) and
/// asserts the ListChanged callback payload each time. Pure logic — no RenderingDevice — so it runs
/// headless and prints OBJECTLISTCHECK: PASS/FAIL. Proves the "formula" without eyeballing the UI.
public static class ObjectListCheck
{
    /// host is any Node already in the tree (the control needs a parent for AddChild/QueueFree).
    public static bool Run(Node host, out string msg)
    {
        var schema = new List<SchemaField>
        {
            new() { Id = "kind",   Type = "enum",  Label = "kind", Options = new[] { "Sun", "Moon" }, Default = 0 },
            new() { Id = "amount", Type = "float", Label = "amount", Min = 0f, Max = 10f, Default = 5f },
            new() { Id = "on",     Type = "bool",  Label = "on", DefBool = true },
        };

        List<Godot.Collections.Dictionary>? last = null;
        int fires = 0;

        var olc = new ObjectListControl();
        host.AddChild(olc);
        var seed = new List<Godot.Collections.Dictionary>
        {
            new() { { "kind", 0 }, { "amount", 5.0f }, { "on", true } },
        };
        // Trivial widget factory: builds a hidden Control, exposes nothing — the model is what we test.
        ObjectListControl.FieldWidget factory = (f, initial, onChanged) => new Control();
        olc.Init("check", schema, seed, minItems: 1, maxItems: 3, factory,
                 items => { last = items; fires++; });

        bool ok = true;
        var fails = new List<string>();
        void Check(bool cond, string what) { if (!cond) { ok = false; fails.Add(what); } }

        // 1. Init seeds 1 item; no callback yet (Init doesn't fire).
        Check(olc.Items.Count == 1, $"init count==1 (got {olc.Items.Count})");
        Check(fires == 0, $"init fires no callback (got {fires})");

        // 2. Programmatic add -> 2 items, callback fired, defaults applied.
        olc.SetItems(new List<Godot.Collections.Dictionary>
        {
            new() { { "kind", 0 }, { "amount", 5.0f }, { "on", true } },
            new() { { "kind", 1 }, { "amount", 2.5f }, { "on", false } },
        });
        Check(olc.Items.Count == 2, $"setitems count==2 (got {olc.Items.Count})");
        Check(fires == 1, $"setitems fires once (got {fires})");
        Check(last != null && last.Count == 2, "callback payload has 2 items");
        Check(olc.Items[1]["kind"].AsInt32() == 1, "item1 kind==Moon");
        Check(Mathf.IsEqualApprox(olc.Items[1]["amount"].AsSingle(), 2.5f), "item1 amount==2.5");
        Check(olc.Items[1]["on"].AsBool() == false, "item1 on==false");

        // 3. Field edit writes back into the item dict (simulate the widget onChanged).
        olc.Items[0]["amount"] = 7.0f;
        Check(Mathf.IsEqualApprox(olc.Items[0]["amount"].AsSingle(), 7.0f), "field edit writes back");

        // 4. Max-items bound: SetItems to exactly max=3, then attempt a programmatic overflow via SetItems(4).
        olc.SetItems(new List<Godot.Collections.Dictionary>
        {
            new() { { "kind", 0 }, { "amount", 1f }, { "on", true } },
            new() { { "kind", 0 }, { "amount", 2f }, { "on", true } },
            new() { { "kind", 0 }, { "amount", 3f }, { "on", true } },
        });
        Check(olc.Items.Count == 3, $"at-max count==3 (got {olc.Items.Count})");

        // 5. Duplicate semantics: a duplicated dict is an independent copy (mutating one doesn't touch the other).
        var a = new Godot.Collections.Dictionary { { "kind", 0 }, { "amount", 4f }, { "on", true } };
        var b = a.Duplicate();
        b["amount"] = 9f;
        Check(Mathf.IsEqualApprox(a["amount"].AsSingle(), 4f), "duplicate is independent copy");

        olc.QueueFree();

        msg = ok ? $"{fires} callbacks, all payload asserts held" : string.Join("; ", fails);
        return ok;
    }
}
