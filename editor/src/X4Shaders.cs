using System;
using System.Collections.Generic;
using Godot;
using X4Data.Materials;

namespace GalaxyEditor3D;

/// <summary>
/// Godot shader for X4's lit hull materials (p1 / p2_complex_surface, p2_complex_projection decals). Own code that
/// follows what X4's material entries say (facts read from the game's material library and shader settings, 2026-11-20):
/// - diffuse_map: colour, times the model's vertex colours (grey). diffuse_detail_map (tiled, e.g. 10 x 5): red multiplies the colour, green / blue overlay
///   smoothness / metalness.
/// - smooth_map red x "Smoothness", metal_map red x "Metallness"; roughness = 1.01 - smoothness.
/// - normal_map: red / green = x / y of the normal, z computed; normal_detail_map adds its x / y (weak, e.g. 0.05).
/// - color_glow_map: rgb glow, alpha boosts it up to 10x; glow_hue (0.5 = unchanged) and glow_saturation tint it.
/// Not used: dirt and paint layers (need X4's per-vertex age data and a second UV set), parallax.
/// </summary>
public static class X4Shaders
{
    /// <summary>The green channel of normal maps points the other way (DirectX style). Toggle with N in the sector view to compare.</summary>
    public static bool FlipNormalGreen { get; private set; } = Dbg("flipnormals");
    /// <summary>Look checks (debug window): single layers on / off for all materials. Start values from X4ED_SHADER=noglow,nometal,novertex,flipnormals.</summary>
    public static bool Glow = !Dbg("noglow"), Metal = !Dbg("nometal"), VertexColours = !Dbg("novertex");
    static bool Dbg(string w) => (System.Environment.GetEnvironmentVariable("X4ED_SHADER") ?? "").Contains(w);

    static Shader? _opaque, _cutout;
    static readonly List<WeakReference<ShaderMaterial>> Made = new();

    const string Body = @"
uniform sampler2D diffuse_map : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_diffuse = false;
uniform vec4 base_color : source_color = vec4(0.55, 0.57, 0.6, 1.0);
uniform sampler2D normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_normal = false;
uniform float normal_strength = 1.0;
uniform bool flip_green = false;
uniform sampler2D smooth_map : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float smoothness = 0.5;
uniform sampler2D metal_map : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform float metalness = 0.3;
uniform sampler2D detail_map : hint_default_white, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_detail = false;
uniform vec2 detail_tiling = vec2(3.0, 3.0);
uniform float detail_strength = 1.0;
uniform sampler2D detail_normal_map : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_detail_normal = false;
uniform vec2 detail_normal_tiling = vec2(3.0, 3.0);
uniform float detail_normal_strength = 1.0;
uniform sampler2D glow_map : source_color, hint_default_black, filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_glow = false;
uniform float glow_strength = 1.0;
uniform float glow_hue = 0.5;
uniform float glow_saturation = 1.0;
uniform float glow_energy = 1.0;
uniform float lift = 0.0;
uniform bool use_vertex_colour = true;
uniform bool glow_on = true;
uniform bool metal_on = true;

float overlay(float a, float b) { return a < 0.5 ? 2.0 * a * b : 1.0 - 2.0 * (1.0 - a) * (1.0 - b); }

// hue turn around the grey axis, then saturation around the grey value
vec3 tint(vec3 c, float hue_turns, float sat) {
    float a = hue_turns * 6.2831853;
    vec3 k = vec3(0.57735);
    c = c * cos(a) + cross(k, c) * sin(a) + k * dot(k, c) * (1.0 - cos(a));
    float grey = dot(c, vec3(0.299, 0.587, 0.114));
    return max(mix(vec3(grey), c, sat), vec3(0.0));
}

vec2 nxy(vec4 t) { vec2 v = t.rg * 2.0 - 1.0; if (flip_green) v.y = -v.y; return v; }

void vertex() { VERTEX += NORMAL * lift; }

void fragment() {
    vec4 d = has_diffuse ? texture(diffuse_map, UV) : base_color;
    vec3 col = d.rgb;
    float sm = smoothness * texture(smooth_map, UV).r;
    float me = metal_on ? metalness * texture(metal_map, UV).r : 0.0;
    if (has_detail) {
        vec4 dd = texture(detail_map, UV * detail_tiling) * detail_strength;
        col *= dd.r; sm = overlay(sm, dd.g); me = overlay(me, dd.b);
    }
    // the model's vertex colours darken / shade the hull texture (grey version, as X4 does without a height map);
    // meshes without vertex colours give white here, i.e. no change
    col *= use_vertex_colour ? vec3(dot(COLOR.rgb, vec3(0.299, 0.587, 0.114))) : vec3(1.0);
    ALBEDO = col;
    METALLIC = clamp(me, 0.0, 1.0);
    ROUGHNESS = clamp(1.01 - sm, 0.04, 1.0);
    if (has_normal) {
        vec2 xy = nxy(texture(normal_map, UV)) * normal_strength;
        vec3 n = vec3(xy, sqrt(max(0.0, 1.0 - dot(xy, xy))));
        if (has_detail_normal) n.xy += nxy(texture(detail_normal_map, UV * detail_normal_tiling)) * detail_normal_strength;
        n = normalize(n);
        NORMAL_MAP = n * 0.5 + 0.5;
    }
    if (has_glow && glow_on) {
        vec4 g = texture(glow_map, UV);
        EMISSION = tint(g.rgb * (1.0 + g.a * 9.0), glow_hue - 0.5, glow_saturation) * glow_strength * glow_energy;
    }
    ALPHA_LINE
}
";

    static Shader Opaque => _opaque ??= new Shader { Code = "shader_type spatial;\nrender_mode blend_mix, depth_draw_opaque, cull_back, specular_schlick_ggx;\n" + Body.Replace("ALPHA_LINE", "") };
    // decals: cut out by the diffuse alpha, lifted a little off the hull so they do not flicker through it
    static Shader Cutout => _cutout ??= new Shader { Code = "shader_type spatial;\nrender_mode blend_mix, depth_draw_opaque, cull_back, specular_schlick_ggx;\n" + Body.Replace("ALPHA_LINE", "ALPHA = d.a; ALPHA_SCISSOR_THRESHOLD = 0.5;") };

    /// <summary>True for the X4 shaders this material imitates (lit surfaces and decals).</summary>
    public static bool Handles(MaterialLibrary.Material x) =>
        x.Shader.Contains("complex_surface") || x.Shader.Contains("complex_projection") || x.Shader.Contains("complex_lit") || x.Shader.StartsWith("xu_surface");

    /// <summary>Builds the Godot material; getTexture loads a game texture (null if missing).</summary>
    public static ShaderMaterial Build(MaterialLibrary.Material x, Func<string, Texture2D?> getTexture)
    {
        bool decal = x.Shader.Contains("projection");
        var m = new ShaderMaterial { Shader = decal ? Cutout : Opaque };
        Texture2D? Tex(params string[] names)
        {
            foreach (var n in names) if (x.Map(n) is { } path && getTexture(path) is { } t) return t;
            return null;
        }
        void Set(string param, Texture2D? t, string flag) { if (t != null) { m.SetShaderParameter(param, t); m.SetShaderParameter(flag, true); } }
        float F(string n, float fallback) => x.Float(n, fallback);
        Vector2 Tiling(string layer, float fallback) => new(F(layer + "_tiling", fallback), F(layer + "_tiling_v", F(layer + "_tiling", fallback)));

        Set("diffuse_map", Tex("diffuse_map", "diffuse", "color_map"), "has_diffuse");
        Set("normal_map", Tex("normal_map"), "has_normal");
        Set("detail_map", Tex("diffuse_detail_map"), "has_detail");
        Set("detail_normal_map", Tex("normal_detail_map"), "has_detail_normal");
        Set("glow_map", Tex("color_glow_map"), "has_glow");
        if (Tex("smooth_map") is { } sm) m.SetShaderParameter("smooth_map", sm);
        if (Tex("metal_map") is { } mt) m.SetShaderParameter("metal_map", mt);
        // the library spells these both ways ("Smoothness" / "smoothness", "Metallness" / "metallness")
        m.SetShaderParameter("smoothness", Math.Clamp(F("Smoothness", 0.5f), 0f, 1f));
        m.SetShaderParameter("metalness", Math.Clamp(F("Metallness", F("metalness", 0.3f)), 0f, 1f));
        m.SetShaderParameter("normal_strength", F("normalStr", 1f));
        m.SetShaderParameter("detail_tiling", Tiling("diffuse_detail", 3f));
        m.SetShaderParameter("detail_strength", F("diffuse_detailStr", 1f));
        m.SetShaderParameter("detail_normal_tiling", Tiling("normal_detail", 3f));
        m.SetShaderParameter("detail_normal_strength", F("normal_detailStr", 1f));
        m.SetShaderParameter("glow_strength", F("color_glowStr", 1f));
        m.SetShaderParameter("glow_hue", F("glow_hue", 0.5f));
        m.SetShaderParameter("glow_saturation", F("glow_saturation", 1f));
        if (decal) m.SetShaderParameter("lift", 0.02f);
        Apply(m);
        lock (Made) Made.Add(new WeakReference<ShaderMaterial>(m));
        return m;
    }

    /// <summary>Current look switches into one material.</summary>
    static void Apply(ShaderMaterial m)
    {
        m.SetShaderParameter("flip_green", FlipNormalGreen);
        m.SetShaderParameter("glow_on", Glow);
        m.SetShaderParameter("metal_on", Metal);
        m.SetShaderParameter("use_vertex_colour", VertexColours);
    }

    /// <summary>Current look switches into every material made so far.</summary>
    public static void ApplyAll()
    {
        lock (Made)
        {
            Made.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in Made) if (w.TryGetTarget(out var m)) Apply(m);
        }
    }

    /// <summary>Flips the normal maps' green channel on every material made so far (and on all new ones).</summary>
    public static void ToggleNormalGreen() { FlipNormalGreen = !FlipNormalGreen; ApplyAll(); }
}
