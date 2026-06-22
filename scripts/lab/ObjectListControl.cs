using Godot;
using System;
using System.Collections.Generic;

namespace WG16.Lab;

/// Generic schema-driven list editor (the "formula"). Renders an add/remove/duplicate/reorder
/// list of objects; each item's fields are built by the shared widget factory (one widget impl
/// per type). Owns its OWN item model (List<Godot.Collections.Dictionary>, Variant values) and
/// item->widget map — it does NOT touch the flat _byId registry. Fires one ListChanged callback
/// on any mutation; a per-list adapter converts dicts -> domain objects.
public sealed partial class ObjectListControl : VBoxContainer
{
    public delegate Control FieldWidget(SchemaField f, Variant initial, Action<Variant> onChanged);

    private List<SchemaField> _schema = new();
    private readonly List<Godot.Collections.Dictionary> _items = new();
    private int _minItems = 1, _maxItems = 7;
    private FieldWidget _widget = null!;
    private Action<List<Godot.Collections.Dictionary>> _onChanged = null!;
    private VBoxContainer _rows = null!;
    private Button _addBtn = null!;

    public List<Godot.Collections.Dictionary> Items => _items;

    public void Init(string label, List<SchemaField> schema, List<Godot.Collections.Dictionary> initialItems,
                     int minItems, int maxItems, FieldWidget widgetFactory,
                     Action<List<Godot.Collections.Dictionary>> onListChanged)
    {
        _schema = schema; _minItems = minItems; _maxItems = maxItems;
        _widget = widgetFactory; _onChanged = onListChanged;
        _items.Clear();
        foreach (var it in initialItems) { _items.Add(it.Duplicate()); }

        AddChild(new Label { Text = label });
        _addBtn = new Button { Text = "+ add" };
        _addBtn.Pressed += () => { AddItem(DefaultItem()); Fire(); };
        AddChild(_addBtn);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(_rows);
        Rebuild();
    }

    /// Replace the whole list (preset load, U3): rebuild + fire the callback.
    public void SetItems(List<Godot.Collections.Dictionary> items)
    {
        _items.Clear();
        foreach (var it in items) { _items.Add(it.Duplicate()); }
        Rebuild();
        Fire();
    }

    private Godot.Collections.Dictionary DefaultItem()
    {
        var d = new Godot.Collections.Dictionary();
        foreach (var f in _schema)
        {
            d[f.Id] = f.Type switch
            {
                "bool" => f.DefBool,
                "color" => f.DefColor,
                "enum" => (int)f.Default,
                _ => f.Default,
            };
        }
        return d;
    }

    private void AddItem(Godot.Collections.Dictionary item)
    {
        if (_items.Count >= _maxItems) { return; }
        _items.Add(item);
        Rebuild();
    }

    private void AddItemAt(int at, Godot.Collections.Dictionary item)
    {
        if (_items.Count >= _maxItems) { return; }
        at = Mathf.Clamp(at, 0, _items.Count);
        _items.Insert(at, item);
        Rebuild();
    }

    private void Swap(int a, int b)
    {
        if (a < 0 || b < 0 || a >= _items.Count || b >= _items.Count) { return; }
        (_items[a], _items[b]) = (_items[b], _items[a]);
        Rebuild();
    }

    private void Fire() => _onChanged(_items);

    /// Rebuild only this control's own sub-tree (not the whole panel).
    private void Rebuild()
    {
        foreach (Node child in _rows.GetChildren()) { child.QueueFree(); }
        _addBtn.Disabled = _items.Count >= _maxItems;

        for (int i = 0; i < _items.Count; i++)
        {
            int idx = i;   // capture
            var item = _items[i];
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };

            // Header: title + duplicate / up / down / remove.
            var head = new HBoxContainer();
            head.AddChild(new Label { Text = ItemTitle(item, idx), CustomMinimumSize = new Vector2(120, 0) });
            var dup = new Button { Text = "⧉" };
            dup.Pressed += () => { AddItemAt(idx + 1, item.Duplicate()); Fire(); };
            head.AddChild(dup);
            var up = new Button { Text = "▲" }; up.Disabled = idx == 0;
            up.Pressed += () => { Swap(idx, idx - 1); Fire(); };
            head.AddChild(up);
            var dn = new Button { Text = "▼" }; dn.Disabled = idx == _items.Count - 1;
            dn.Pressed += () => { Swap(idx, idx + 1); Fire(); };
            head.AddChild(dn);
            var rem = new Button { Text = "✕" }; rem.Disabled = _items.Count <= _minItems;
            rem.Pressed += () => { _items.RemoveAt(idx); Rebuild(); Fire(); };
            head.AddChild(rem);
            box.AddChild(head);

            // Fields: reuse the shared widget factory; write back into this item dict.
            foreach (var f in _schema)
            {
                var row = new HBoxContainer();
                row.AddChild(new Label { Text = f.Label, CustomMinimumSize = new Vector2(90, 0) });
                Variant initial = item.ContainsKey(f.Id) ? item[f.Id] : default;
                SchemaField field = f;   // capture
                var w = _widget(field, initial, v => { item[field.Id] = v; Fire(); });
                row.AddChild(w);
                box.AddChild(row);
            }
            box.AddChild(new HSeparator());
            _rows.AddChild(box);
        }
    }

    /// A readable per-item title from the first enum (kind), if present; else the index.
    private string ItemTitle(Godot.Collections.Dictionary item, int idx)
    {
        foreach (var f in _schema)
        {
            if (f.Type == "enum" && item.ContainsKey(f.Id))
            {
                int sel = item[f.Id].AsInt32();
                if (sel >= 0 && sel < f.Options.Length) { return $"{f.Options[sel]} {idx}"; }
            }
        }
        return $"item {idx}";
    }
}
