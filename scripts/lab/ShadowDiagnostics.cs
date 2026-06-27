using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// Central shadow-baseline audit. This does not own rendering; it reports whether any scene/runtime path has
/// reintroduced shadow lights, mesh casters, or screen-space/GI occlusion behind the review baseline.
public static class ShadowDiagnostics
{
    public sealed class Snapshot
    {
        public int ShadowLights;
        public int VisibleShadowLights;
        public int GeometryCasters;
        public int VisibleGeometryCasters;
        public int CdlodShadowCasters;
        public int TerrainShaderOwners;
        public bool Ssao;
        public bool Ssil;
        public bool Sdfgi;
        public long ShadowDraws;
        public long ShadowObjects;
        public long ShadowPrimitives;
        public string Owners = "none";

        public bool Clean =>
            ShadowLights == 0 &&
            GeometryCasters == 0 &&
            CdlodShadowCasters == 0 &&
            TerrainShaderOwners == 0 &&
            !Ssao && !Ssil && !Sdfgi &&
            ShadowDraws == 0 &&
            ShadowObjects == 0 &&
            ShadowPrimitives == 0;

        public string ToProfileLine(string prefix)
        {
            return $"{prefix}: clean={(Clean ? "YES" : "NO")} lights={ShadowLights}/{VisibleShadowLights} " +
                   $"geomCasters={GeometryCasters}/{VisibleGeometryCasters} cdlodCasters={CdlodShadowCasters} " +
                   $"terrainShaderOwners={TerrainShaderOwners} " +
                   $"ssao={Ssao} ssil={Ssil} sdfgi={Sdfgi} " +
                   $"render draws={ShadowDraws} objects={ShadowObjects} prim={ShadowPrimitives} owners={Owners}";
        }
    }

    public static Snapshot Capture(Node context)
    {
        var s = new Snapshot();
        if (context == null) { return s; }

        Viewport? vp = context.GetViewport();
        if (vp != null)
        {
            s.ShadowDraws = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.DrawCallsInFrame);
            s.ShadowObjects = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.ObjectsInFrame);
            s.ShadowPrimitives = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.PrimitivesInFrame);
        }

        Node root = context.GetNodeOrNull<Node>("/root/TerrainLabRoot") ?? context.GetTree().Root;
        var owners = new List<string>(8);
        Walk(root, s, owners);

        var cd = context.GetNodeOrNull<CdlodTerrain>("/root/TerrainLabRoot/CdlodTerrain");
        if (cd != null && cd.Enabled)
        {
            cd.ActiveDiagnostics(out _, out _, out int shadowCasters, out _);
            s.CdlodShadowCasters = shadowCasters;
            if (shadowCasters > 0) { AddOwner(owners, "cdlod", cd); }
        }

        var terrain = context.GetNodeOrNull<TerrainLab>("/root/TerrainLabRoot/TerrainLab");
        if (terrain?.MaterialOverride is ShaderMaterial mat)
        {
            Variant hz = mat.GetShaderParameter("hz_on");
            if (hz.VariantType == Variant.Type.Bool && hz.AsBool())
            {
                s.TerrainShaderOwners++;
                AddOwner(owners, "terrain:horizon", terrain);
            }
        }

        s.Owners = owners.Count == 0 ? "none" : string.Join(",", owners);
        return s;
    }

    private static void Walk(Node node, Snapshot s, List<string> owners)
    {
        if (node is Light3D light && light.ShadowEnabled)
        {
            s.ShadowLights++;
            if (light.IsVisibleInTree()) { s.VisibleShadowLights++; }
            AddOwner(owners, "light", node);
        }

        if (node is GeometryInstance3D geom && geom.CastShadow != GeometryInstance3D.ShadowCastingSetting.Off)
        {
            s.GeometryCasters++;
            if (geom.IsVisibleInTree()) { s.VisibleGeometryCasters++; }
            AddOwner(owners, "caster", node);
        }

        if (node is WorldEnvironment we && we.Environment != null)
        {
            s.Ssao |= we.Environment.SsaoEnabled;
            s.Ssil |= we.Environment.SsilEnabled;
            s.Sdfgi |= we.Environment.SdfgiEnabled;
            if (we.Environment.SsaoEnabled) { AddOwner(owners, "ssao", node); }
            if (we.Environment.SsilEnabled) { AddOwner(owners, "ssil", node); }
            if (we.Environment.SdfgiEnabled) { AddOwner(owners, "sdfgi", node); }
        }

        foreach (Node child in node.GetChildren()) { Walk(child, s, owners); }
    }

    private static void AddOwner(List<string> owners, string kind, Node node)
    {
        if (owners.Count >= 8) { return; }
        owners.Add($"{kind}:{node.GetPath()}");
    }
}
