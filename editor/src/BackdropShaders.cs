using System;
using Godot;
using X4Data.Materials;

namespace GalaxyEditor3D;

/// <summary>
/// Godot shaders for X4's backdrop materials (sky, stars, nebulae, suns, atmospheres, rings). Own code that follows
/// what the material entries say; X4's own shaders are not readable, so the look is approximated and tuned against
/// in-game screenshots (2026-11-22: Midnight Star, cluster_703_v1).
/// Facts from the material library:
/// - p1_stars ADDITIVE: diffuse_map (nebula or star atlas, no alpha), diffuse2_map optional mask, color_emissive, diffuseStr.
/// - p1_starfield ADDITIVE: three layers, diffuse_map (tiling diffuse_tiling), diffuse2_map (diffuse2_tiling),
///   diffuse3_map = mask of layer 1 (diffuse3_tiling).
/// - p1_simple_unlit NONE: sky sphere, painted by the mesh's vertex colours times diffuse_map.
/// - p1_complex_unlit ADDITIVE: glows, sun shells, coronas, flares: diffuse_map x color_emissive x diffuseStr,
///   "angle_fade": fades where the surface is seen edge-on (shells) or face-on.
/// - p1_complex_unlit ALPHA8: planet rings: diffuse_map with alpha, diffuse2_map tint.
/// - p1_planet_sun: diffuse_map (one channel, the "flakes") read through color_height_map (a heat gradient).
/// - p1_planet_atmosphere_scattering: thin shell; colour color_Atmo, seen as a rim.
/// X4's textures for additive layers carry no alpha: these shaders never use it there (the old fallback multiplied
/// by alpha = 0 and drew nothing).
/// </summary>
public static class BackdropShaders
{
    const string Common = @"
vec3 shift(vec3 c, float hue_deg, float sat, float bright, float contrast) {
    float a = radians(hue_deg);
    vec3 k = vec3(0.57735);
    c = c * cos(a) + cross(k, c) * sin(a) + k * dot(k, c) * (1.0 - cos(a));
    float grey = dot(c, vec3(0.299, 0.587, 0.114));
    c = mix(vec3(grey), c, sat);
    c = (c - 0.5) * contrast + 0.5;
    return max(c * bright, vec3(0.0));
}
";

    // additive glow: nebulae, star atlas, glows, shells, coronas, flares
    const string GlowCode = "shader_type spatial;\nrender_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled;\n" + Common + @"
uniform sampler2D tex1 : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has1 = false;
uniform vec2 tiling1 = vec2(1.0);
uniform sampler2D tex2 : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has2 = false;
uniform vec2 tiling2 = vec2(1.0);
uniform vec3 tint = vec3(1.0);
uniform float strength = 1.0;
uniform bool use_vcol = true;
uniform float edge_fade = 0.0;     // > 0: fades where the surface is seen edge-on (shells)
uniform float mask_blur = 0.0;     // > 0: the second texture is a mask, read from a smaller (blurred) mip level
uniform float hue = 0.0;
uniform float sat = 1.0;
uniform float bright = 1.0;
uniform float contrast = 1.0;
void fragment() {
    vec3 c = has1 ? texture(tex1, UV * tiling1).rgb : vec3(1.0);
    if (has2) c *= mask_blur > 0.0 ? smoothstep(vec3(0.0), vec3(1.0), textureLod(tex2, UV * tiling2, mask_blur).rgb) : texture(tex2, UV * tiling2).rgb;
    c = shift(c, hue, sat, bright, contrast) * tint * strength;
    if (use_vcol) c *= COLOR.rgb;
    if (edge_fade > 0.0) c *= pow(clamp(abs(dot(NORMAL, VIEW)), 0.0, 1.0), edge_fade);
    ALBEDO = c;
}
";

    // three-layer starfield
    const string StarfieldCode = "shader_type spatial;\nrender_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled;\n" + @"
// no mipmaps: far away a star is smaller than a pixel, and the smaller mip levels average it into grey haze
uniform sampler2D layer1 : source_color, filter_linear, repeat_enable;
uniform sampler2D layer2 : source_color, filter_linear, repeat_enable;
uniform sampler2D mask1 : filter_linear_mipmap, repeat_enable;
uniform bool has2 = false;
uniform bool has_mask = false;
uniform float t1 = 1.0;
uniform float t2 = 1.0;
uniform float t3 = 1.0;
uniform float boost1 = 1.0;
uniform float boost2 = 0.3;
uniform vec3 tint = vec3(1.0);
void fragment() {
    vec3 c = max(texture(layer1, UV * t1).rgb - 0.025, vec3(0.0)) * 3.0 * boost1;
    if (has_mask) c *= mix(0.35, 1.0, texture(mask1, UV * t3).r);
    if (has2) c += max(texture(layer2, UV * t2).rgb - 0.025, vec3(0.0)) * 3.0 * boost2;
    ALBEDO = c * tint * COLOR.rgb;
}
";

    // sky sphere: vertex colours x texture, drawn behind everything
    const string SkyCode = "shader_type spatial;\nrender_mode unshaded, cull_disabled, depth_draw_never, fog_disabled;\n" + @"
uniform sampler2D tex1 : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has1 = false;
uniform float strength = 1.0;
void fragment() {
    vec3 c = has1 ? texture(tex1, UV).rgb : vec3(1.0);
    // the vertex colours are stored as display colours (sRGB): linear first, else the sky comes out mid-blue
    vec3 v = clamp(COLOR.rgb, vec3(0.0), vec3(1.0));
    ALBEDO = clamp(c * v * v * strength, vec3(0.0), vec3(1.0));     // square: close to sRGB -> linear, never NaN
    ALPHA = 1.0;     // transparent pass, drawn first (render priority): never over the nebulae
}
";

    // planet rings: texture alpha, second texture as tint; a little darker than the texture (no light model)
    const string RingCode = "shader_type spatial;\nrender_mode unshaded, blend_mix, cull_disabled, depth_draw_never, fog_disabled;\n" + @"
uniform sampler2D tex1 : source_color, filter_linear_mipmap, repeat_enable;
uniform sampler2D tex2 : source_color, filter_linear_mipmap, repeat_enable;
uniform bool has2 = false;
uniform vec3 tint = vec3(1.0);
uniform float strength = 0.55;
void fragment() {
    vec4 t = texture(tex1, UV);
    vec3 c = t.rgb;
    if (has2) c *= mix(vec3(1.0), texture(tex2, UV).rgb, 0.5);
    ALBEDO = c * tint * strength;
    ALPHA = t.a * 0.85;
}
";

    // sun surface: flakes read through the heat gradient
    const string SunCode = "shader_type spatial;\nrender_mode unshaded, cull_back, fog_disabled;\n" + Common + @"
uniform sampler2D flakes : filter_linear_mipmap, repeat_enable;
uniform sampler2D detail : filter_linear_mipmap, repeat_enable;
uniform bool has_detail = false;
uniform vec2 detail_tiling = vec2(4.0, 2.0);
uniform sampler2D gradient : source_color, filter_linear, repeat_disable;
uniform bool has_gradient = false;
uniform float strength = 3.0;
uniform float bright = 1.0;
void fragment() {
    float v = texture(flakes, UV).r;
    if (has_detail) v = clamp(v * 0.6 + texture(detail, UV * detail_tiling).r * 0.5, 0.0, 1.0);
    vec3 c = has_gradient ? texture(gradient, vec2(clamp(v, 0.02, 0.98), 0.5)).rgb : vec3(1.0, 0.8, 0.5) * v;
    // limb darkening: the disc's edge is a little darker
    c *= mix(0.55, 1.0, pow(clamp(dot(NORMAL, VIEW), 0.0, 1.0), 0.4));
    ALBEDO = c * strength * bright;
}
";

    // atmosphere: a coloured rim
    const string AtmoCode = "shader_type spatial;\nrender_mode unshaded, blend_add, cull_disabled, depth_draw_never, fog_disabled;\n" + @"
uniform vec3 colour = vec3(0.35, 0.55, 1.0);
uniform float strength = 1.0;
void fragment() {
    float rim = 1.0 - clamp(abs(dot(NORMAL, VIEW)), 0.0, 1.0);
    ALBEDO = colour * pow(rim, 2.5) * strength;
}
";

    static Shader? _glow, _starfield, _sky, _ring, _sun, _atmo;
    static Shader S(ref Shader? s, string code) => s ??= new Shader { Code = code };

    /// <summary>True for the backdrop shaders these imitate.</summary>
    public static bool Handles(MaterialLibrary.Material x) =>
        x.Shader is "p1_stars" or "p1_starfield" or "p1_simple_unlit" or "p1_complex_unlit" or "p1_planet_sun"
        || x.BlendMode == "PLANET_ATMOSPHERE" || x.Shader == "p1_planet_atmosphere_scattering";

    /// <summary>The Godot material for a backdrop material; null when it is none of those.</summary>
    public static Material? Build(MaterialLibrary.Material x, Func<string, Texture2D?> tex)
    {
        if (!Handles(x)) return null;
        Texture2D? T(string n) => x.Map(n) is { } p ? tex(p) : null;
        float F(string n, float f) => x.Float(n, f);
        Vector3 C(string n, Vector3 f) => x.Color(n) is { } c ? new Vector3(c.R, c.G, c.B) : f;
        Vector2 Tile(string layer) => new(F(layer + "_tiling", 1f), F(layer + "_tiling_v", F(layer + "_tiling", 1f)));
        bool additive = x.BlendMode is "ADDITIVE" or "SCREEN";
        // strong strengths (diffuseStr 40 on a sun shell) are meant for X4's HDR exposure: kept, but softened
        float Str(float s) => s <= 1f ? s : 1f + MathF.Log(s);

        if (x.Shader == "p1_planet_atmosphere_scattering" || x.BlendMode == "PLANET_ATMOSPHERE")
        {
            var m = new ShaderMaterial { Shader = S(ref _atmo, AtmoCode) };
            m.SetShaderParameter("colour", C("color_Atmo", new Vector3(0.35f, 0.55f, 1f)) * 1.6f);
            m.SetShaderParameter("strength", Math.Clamp(F("fDensity", 1f), 0.3f, 1.5f));
            return m;
        }
        if (x.Shader == "p1_planet_sun")
        {
            var m = new ShaderMaterial { Shader = S(ref _sun, SunCode) };
            if (T("diffuse_map") is { } f) m.SetShaderParameter("flakes", f);
            if (T("diffuse_detail_map") is { } d) { m.SetShaderParameter("detail", d); m.SetShaderParameter("has_detail", true); m.SetShaderParameter("detail_tiling", Tile("diffuse_detail")); }
            if (T("color_height_map") is { } gr) { m.SetShaderParameter("gradient", gr); m.SetShaderParameter("has_gradient", true); }
            m.SetShaderParameter("strength", Str(F("diffuseStr", 3f)));
            m.SetShaderParameter("bright", F("base_brightness_shift", 1f));
            return m;
        }
        if (x.Shader == "p1_starfield")
        {
            var m = new ShaderMaterial { Shader = S(ref _starfield, StarfieldCode) };
            if (T("diffuse_map") is { } l1) m.SetShaderParameter("layer1", l1);
            if (T("diffuse2_map") is { } l2) { m.SetShaderParameter("layer2", l2); m.SetShaderParameter("has2", true); }
            if (T("diffuse3_map") is { } mk) { m.SetShaderParameter("mask1", mk); m.SetShaderParameter("has_mask", true); }
            m.SetShaderParameter("t1", F("diffuse_tiling", 1f));
            m.SetShaderParameter("t2", F("diffuse2_tiling", 1f));
            m.SetShaderParameter("t3", F("diffuse3_tiling", 1f));
            m.SetShaderParameter("boost1", F("layer1_boostmax", 1f));
            m.SetShaderParameter("boost2", 0.35f * F("layer2_boostmax", 1f));
            m.SetShaderParameter("tint", C("color_emissive", Vector3.One));
            m.RenderPriority = -120;
            return m;
        }
        if (x.Shader == "p1_simple_unlit" && !additive)
        {
            var m = new ShaderMaterial { Shader = S(ref _sky, SkyCode) };
            if (T("diffuse_map") is { } d) { m.SetShaderParameter("tex1", d); m.SetShaderParameter("has1", true); }
            m.SetShaderParameter("strength", F("diffuseStr", 1f));
            m.RenderPriority = -128;
            return m;
        }
        if (x.Shader == "p1_complex_unlit" && x.BlendMode.StartsWith("ALPHA"))
        {
            var m = new ShaderMaterial { Shader = S(ref _ring, RingCode) };
            if (T("diffuse_map") is { } d) m.SetShaderParameter("tex1", d);
            if (T("diffuse2_map") is { } d2) { m.SetShaderParameter("tex2", d2); m.SetShaderParameter("has2", true); }
            m.SetShaderParameter("tint", C("color_emissive", Vector3.One));
            return m;
        }
        if (!additive && x.Shader != "p1_stars") return null;      // opaque complex_unlit etc.: the general fallback

        // additive: p1_stars, p1_complex_unlit glows, p1_simple_unlit nebula layers
        {
            var m = new ShaderMaterial { Shader = S(ref _glow, GlowCode) };
            if (T("diffuse_map") is { } d) { m.SetShaderParameter("tex1", d); m.SetShaderParameter("has1", true); m.SetShaderParameter("tiling1", Tile("diffuse")); }
            // p1_stars' diffuse2_map: a low-resolution mask; read blurred (GlowCode, mask_blur), else stretched over
            // the sky it gives hard, stepped cloud edges
            if (T("diffuse2_map") is { } d2 && x.Shader == "p1_stars") { m.SetShaderParameter("tex2", d2); m.SetShaderParameter("has2", true); m.SetShaderParameter("tiling2", Tile("diffuse2")); m.SetShaderParameter("mask_blur", 4f); }

            m.SetShaderParameter("tint", C("color_emissive", Vector3.One));
            m.SetShaderParameter("strength", Str(F("diffuseStr", 1f)) * (x.Shader == "p1_stars" ? 2.2f : 1f));
            m.SetShaderParameter("edge_fade", x.Floats.ContainsKey("angle_fade_speed") || F("angle_fade", 0f) > 0.5f ? 1.5f : 0f);
            m.SetShaderParameter("hue", F("base_hue_shift", 0f));
            m.SetShaderParameter("sat", F("base_saturation_shift", 1f));
            m.SetShaderParameter("bright", F("base_brightness_shift", 1f));
            m.SetShaderParameter("contrast", F("base_contrast_shift", 1f));
            if (x.Shader is "p1_stars" or "p1_simple_unlit") m.RenderPriority = -110;     // nebula layers: right after sky and stars
            return m;
        }
    }
}
