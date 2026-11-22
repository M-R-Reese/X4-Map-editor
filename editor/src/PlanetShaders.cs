using System;
using System.Linq;
using Godot;
using X4Data.Materials;

namespace GalaxyEditor3D;

/// <summary>
/// Godot shaders for X4's planets in the backdrops: p1_planet_earthlike (land, water, ice, clouds, city lights),
/// p1_planet_moon (rock, plains, haze) and p1_planet_atmosphere_scattering (the glowing shell around them).
/// Own code. The method was learned from reading X4's own planet shaders in shadergl (2026-11-22; Egosoft's code, not
/// copied, not shipped) and follows Sean O'Neil's published atmosphere scattering (GPU Gems 2, chapter 16), which X4 uses.
/// Facts used:
/// - The surface is coloured from a HEIGHT map (diffuse_map, red channel): below waterlevel = color_water, above it
///   the height picks a colour from color_height_map, color_climate_map tints by latitude; polar_map's alpha gives
///   the ice caps (polarscale = where they start); citymap / color_glow_map = city lights on the night side.
/// - Textures are read as they are stored (no sRGB decode); the result is turned into linear colour at the end.
/// - A thin atmosphere is computed per vertex (two samples on the planet, three on the shell): in-scatter (blue haze,
///   orange at the day / night line where the light has crossed much air) and the light left after the air.
///   The planet's own radii (fInnerRadius, fOuterRadius, metres) are the mesh's radii (checked: Argon Prime).
/// - Clouds: a mask (where) and cloud textures (how), on the mesh's second UV set, drifting with cloud_speed; the
///   atmosphere colour color_atmo with its ALPHA is a haze over the whole disc, thicker at the rim.
/// - Ground scattering in X4 drops the camera term (its radii are metres there, so that factor becomes 0): same here.
/// The sun's direction and colour come from the backdrop's sun (SectorView.SunFrom -> SetSun).
/// </summary>
public static class PlanetShaders
{
    const string SunDir = "x4_sun_dir", SunLight = "x4_sun_light", Ambient = "x4_ambient";
    static bool _globals;

    /// <summary>Registers the shared sun uniforms once (before the first planet shader is compiled).</summary>
    static void Globals()
    {
        if (_globals) return;
        _globals = true;
        RenderingServer.GlobalShaderParameterAdd(SunDir, RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0, 0, 1));
        RenderingServer.GlobalShaderParameterAdd(SunLight, RenderingServer.GlobalShaderParameterType.Vec3, Vector3.One);
        RenderingServer.GlobalShaderParameterAdd(Ambient, RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.05f, 0.07f, 0.12f));
    }

    /// <summary>Direction TO the sun (world, backdrop space), its light (colour x intensity) and the backdrop's ambient
    /// light (colour x intensity; tints the night side).</summary>
    public static void SetSun(Vector3 toSun, Vector3 light, Vector3 ambient)
    {
        Globals();
        RenderingServer.GlobalShaderParameterSet(SunDir, toSun.Normalized());
        RenderingServer.GlobalShaderParameterSet(SunLight, light);
        RenderingServer.GlobalShaderParameterSet(Ambient, ambient);
    }

    // ---- shared: sun uniforms and the scattering helpers
    const string Common = @"
global uniform vec3 x4_sun_dir;
global uniform vec3 x4_sun_light;
global uniform vec3 x4_ambient;
uniform float r_inner = 1.0;        // planet radius, metres (object space)
uniform float r_outer = 1.025;      // top of the atmosphere, metres
uniform float esun = 4.0;           // sun strength for the scattering
uniform vec3 inv_wave4 = vec3(5.6, 9.5, 19.6);   // 1 / wavelength^4 (red, green, blue)
const float KR = 0.0025;            // Rayleigh (small particles: blue)
const float KM = 0.0010;            // Mie (dust: white)
const float AIR_DEPTH = 0.25;           // height (part of the atmosphere) where the air is thinned to 1/e
const float PI4 = 12.566371;

// optical depth of the air seen at an angle (cos to the vertical); O'Neil's curve fit
float air(float c) {
    float x = 1.0 - c;
    return AIR_DEPTH * exp(-0.00287 + x * (0.459 + x * (3.83 + x * (-6.80 + x * 5.25))));
}

// position and camera in planet radii (centre = 0, surface = 1): float precision stays fine at 100,000 km
void planet_space(mat4 model, vec3 vtx, mat4 inv_view, out vec3 p, out vec3 cam) {
    vec3 centre = model[3].xyz;
    float s = length(model[0].xyz) * r_inner;
    p = ((model * vec4(vtx, 1.0)).xyz - centre) / s;
    cam = (inv_view[3].xyz - centre) / s;
}

// where the view ray enters the atmosphere (0 when the camera is inside it)
float enter(vec3 cam, vec3 ray, float top) {
    float b = dot(cam, ray);
    float c = dot(cam, cam) - top * top;
    return max(-b - sqrt(max(b * b - c, 0.0)), 0.0);
}
";

    // ---- planet and moon surface
    const string SurfaceCode = "shader_type spatial;\nrender_mode unshaded, cull_back, fog_disabled;\n" + Common + @"
uniform bool is_moon = false;
uniform sampler2D height_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform float height_str = 1.0;
uniform float offset_u = 0.0;
uniform sampler2D detail_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_detail = false;
uniform vec2 detail_tiling = vec2(3.0);
uniform float detail_str = 1.0;
uniform vec2 detail_offset = vec2(0.0);
uniform sampler2D polar_map : filter_linear_mipmap, repeat_enable;
uniform bool has_polar = false;
uniform float polar_scale = 0.5;
uniform float ice_col_scale = 1.0;
uniform vec3 water = vec3(0.1, 0.1, 0.2);
uniform vec3 plains = vec3(0.5);
uniform float water_level = 0.15;
uniform sampler2D height_grad : filter_linear, repeat_disable;
uniform bool has_height_grad = false;
uniform sampler2D climate_grad : filter_linear, repeat_enable;
uniform bool has_climate = false;
uniform sampler2D glow_map : filter_linear_mipmap, repeat_enable;
uniform bool has_glow = false;
uniform vec2 city_tiling = vec2(1.0);
uniform float glow_str = 1.0;
uniform float city_level = 0.15;
uniform sampler2D normal_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_normal = false;
uniform float normal_str = 1.0;
uniform sampler2D normal_detail_map : filter_linear_mipmap_anisotropic, repeat_enable;
uniform bool has_normal_detail = false;
uniform vec2 normal_detail_tiling = vec2(4.0);
uniform float normal_detail_str = 1.0;
uniform vec2 normal_sign = vec2(1.0, -1.0);   // tangent directions of the maps on the sphere
uniform sampler2D cloud_mask : filter_linear_mipmap, repeat_enable;
uniform bool has_clouds = false;
uniform float cloud_mask_str = 1.3;
uniform sampler2D cloud_detail_mask : filter_linear_mipmap, repeat_enable;
uniform bool has_cloud_detail = false;
uniform vec2 cloud_detail_tiling = vec2(3.0);
uniform float cloud_detail_str = 1.3;
uniform vec2 cloud_offset = vec2(0.0);           // u of the clouds, v unused
uniform vec2 cloud_detail_offset = vec2(0.0);
uniform sampler2D cloud_tex : filter_linear_mipmap, repeat_enable;
uniform sampler2D dense_tex : filter_linear_mipmap, repeat_enable;
uniform float cloud_cut = 0.2;
uniform float dense_cut = 0.5;
uniform float cloud_speed = 0.0012;
uniform float cloud_tint = 0.0;                  // how much the clouds take the atmosphere colour
uniform vec4 atmo = vec4(0.6, 0.7, 1.0, 0.2);   // haze colour; alpha = its thickness
uniform float density = 1.0;                     // how much the scattering colours the surface
uniform float hue = 0.0;
uniform float sat = 1.0;
uniform float bright = 1.0;
uniform float contrast = 1.0;
uniform float light = 1.0;                       // sun light on the surface (tuned against the game)
uniform float knee = 0.62;                       // highlights above this are compressed ...
uniform float shoulder = 0.14;                  // ... to at most knee + shoulder (tuned against the game)
uniform float ambient = 3.0;                     // backdrop ambient on the surface (x3: night side as in the game, Argon Prime)

varying vec3 v_in;      // in-scattered light
varying vec3 v_left;    // light left after crossing the air
varying vec3 v_n;       // surface normal (world)
varying vec3 v_eye;     // towards the camera (world)
varying vec3 v_pole;    // the planet's axis (world)

void vertex() {
    vec3 p, cam;
    planet_space(MODEL_MATRIX, VERTEX, INV_VIEW_MATRIX, p, cam);
    float top = r_outer / r_inner;
    float thick = 1.0 / (top - 1.0);
    vec3 n = normalize(p);
    vec3 sun = normalize(x4_sun_dir);
    vec3 ray = p - cam;
    float far = length(ray);
    ray /= far;
    float near = enter(cam, ray, top);
    float len = (far - near) * 0.5;               // two samples
    float k = air(dot(sun, n)) + air(dot(-ray, n));
    vec3 loss = inv_wave4 * KR * PI4 + KM * PI4;
    vec3 sum = vec3(0.0), left = vec3(1.0);
    for (int i = 0; i < 2; i++) {
        vec3 q = cam + ray * (near + len * (float(i) + 0.5));
        float d = exp((1.0 - length(q)) * thick / AIR_DEPTH);
        left = exp(-d * k * loss);
        sum += left * d * len * thick;
    }
    v_in = sum * (inv_wave4 * KR * esun + KM * esun);
    v_left = left;
    v_n = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
    v_eye = normalize(INV_VIEW_MATRIX[3].xyz - (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz);
    v_pole = normalize(MODEL_MATRIX[1].xyz);
}

float lum(vec3 c) { return dot(c, vec3(0.299, 0.587, 0.114)); }
vec3 screen(vec3 a, vec3 b) { return 1.0 - (1.0 - a) * (1.0 - b); }
// dark a: multiply, bright a: screen (switch around a brightness of 0.45)
vec3 overlay(vec3 a, vec3 b) { return mix(2.0 * a * b, 1.0 - 2.0 * (1.0 - a) * (1.0 - b), clamp(10.0 * (lum(a) - 0.45), 0.0, 1.0)); }
vec3 climate(vec3 a, vec3 b) { return all(greaterThan(a, vec3(0.99))) ? vec3(1.0) : a * b + (a + b) * 0.25; }

// brightness, contrast, saturation, hue (degrees, turned around the grey axis)
vec3 grade(vec3 c) {
    c *= bright;
    c = (c - 0.5) * contrast + 0.5;
    c = mix(vec3(lum(c)), c, sat);
    float a = radians(hue);
    vec3 k = vec3(0.57735);
    return c * cos(a) + cross(k, c) * sin(a) + k * dot(k, c) * (1.0 - cos(a));
}

vec2 nxy(vec4 t) { return (t.rg * 2.0 - 1.0) * normal_sign; }

// clouds at a point of the second UV set: rgb colour, a cover; base = the haze below them
vec4 clouds(vec2 uv, float t, vec4 base) {
    float m = cloud_mask_str * texture(cloud_mask, uv + vec2(cloud_offset.x + t, t * 0.5)).r;
    if (has_cloud_detail) {
        vec2 duv = uv * cloud_detail_tiling + vec2(cloud_offset.x + t * 1.3 * cloud_detail_tiling.x, cloud_detail_offset.y - t * 0.125 * cloud_detail_tiling.y);
        m = (m + cloud_detail_str * cloud_detail_str * texture(cloud_detail_mask, duv).r) / (cloud_mask_str + cloud_detail_str);
    }
    m = clamp(m, 0.0, 1.0);
    float thin = smoothstep(cloud_cut - 0.4, cloud_cut, m);
    float dense = smoothstep(dense_cut - 0.4, dense_cut, m);
    float s = t * 0.75;
    vec4 c = base;
    c = max(c, thin * texture(cloud_tex, uv + vec2(cloud_offset.x + s, -s * 0.1)));
    c = max(c, thin * texture(cloud_tex, uv + vec2(cloud_offset.x + 0.25 + s, 0.15 - s * 0.1)));
    c = max(c, cloud_detail_str * thin * texture(cloud_tex, uv * cloud_detail_tiling + vec2(cloud_offset.x + s * 0.9 * cloud_detail_tiling.x, cloud_detail_offset.y + s * 0.1 * cloud_detail_tiling.y)));
    c = max(c, dense * texture(dense_tex, uv + vec2(cloud_offset.x + 0.5 + s, 0.125)));
    c = max(c, dense * texture(dense_tex, uv + vec2(cloud_offset.x - 0.15 + s, -0.25)));
    c = max(c, dense * texture(dense_tex, uv + vec2(cloud_offset.x + 0.25 + s, 0.0)));
    return c;
}

void fragment() {
    float t = TIME * cloud_speed;
    vec2 uv = UV + vec2(t * 0.5 + offset_u, 0.0);      // the surface turns slowly
    vec3 h = height_str * texture(height_map, uv).rgb;
    float hs = height_str;
    if (has_detail) {
        h += texture(detail_map, UV * detail_tiling + detail_offset).rgb * detail_str;
        hs += detail_str;
        h /= hs;
    }
    h = clamp(h, 0.0, 1.0);

    // ice caps: the polar map's alpha against polar_scale, a little higher where the land is high
    vec4 polar = has_polar ? texture(polar_map, UV) : vec4(0.0);
    if (is_moon) polar.rgb = polar.rgb * ice_col_scale + plains * 0.5 * (1.0 - ice_col_scale);
    float ice = polar_scale < 1.0 ? smoothstep(polar_scale - 0.01, polar_scale, polar.a + (h.r + 0.2) * 0.2) : 0.0;

    vec3 col;
    vec3 n = normalize(v_n);
    float shine = 0.0;            // water reflects the sun a little
    float relief = 1.0;           // moon plains are smoother
    vec3 glow = vec3(0.0);
    if (is_moon) {
        float f = smoothstep(water_level * 0.7, water_level, (h.r + h.g + h.b) / 3.0);
        col = mix(overlay(h, plains), h, f);
        relief = f * 0.75 + 0.25;
    } else if (h.r < water_level) {
        col = max(water, water * (2.01 - water_level + h.r * 1.52)) + max(vec3(0.0), (h.r - water_level * 0.85) * vec3(1.0, 1.0, 0.5));
        shine = 0.5 * (1.0 - ice);
    } else {
        col = h;
        vec3 hc = has_height_grad ? texture(height_grad, vec2(clamp((h.r - water_level) / (1.0 - water_level), 0.01, 0.99), 0.5)).rgb : vec3(1.0);
        col *= hc;
        if (has_climate) col = climate(col, texture(climate_grad, vec2(UV.x * 2.0, UV.y + sign(UV.y - 0.5) * (0.5 - polar_scale * 0.5))).rgb);
        // city lights: where the glow map's alpha is above 1 - city_level
        if (has_glow) {
            vec4 g = texture(glow_map, UV * city_tiling + detail_offset);
            glow = g.rgb * glow_str * step(1.0 - city_level, g.a);
        }
        ice = clamp(ice + smoothstep(0.8, 0.99, hc.b), 0.0, 1.0);     // snow on the mountain tops
    }
    col = mix(col, polar.rgb * 1.5, ice);
    col = clamp(grade(col), 0.0, 1.0);

    // relief from the normal maps (not on water, less on ice), on the sphere's east / north directions
    vec3 nn = n;
    if (has_normal && (is_moon || h.r >= water_level)) {
        vec2 d = nxy(texture(normal_map, uv)) * normal_str * relief;
        if (has_normal_detail) {
            vec2 dd = nxy(texture(normal_detail_map, UV * detail_tiling + detail_offset)) * (is_moon ? 1.0 : normal_str);
            dd += nxy(texture(normal_detail_map, UV * normal_detail_tiling + detail_offset)) * 0.75;
            d += dd * normal_detail_str * relief;
        }
        vec3 east = normalize(cross(v_pole, n));
        vec3 north = cross(n, east);
        nn = normalize(n + east * d.x + north * d.y);
        nn = normalize(mix(nn, n, ice * 0.5));
    }

    // sunlight
    vec3 sun = normalize(x4_sun_dir);
    vec3 eye = normalize(v_eye);
    float lit = max(dot(nn, sun), 0.0);
    vec3 surf = col * (x4_sun_light * lit * light + x4_ambient * ambient);
    if (shine > 0.0) surf += x4_sun_light * light * shine * 0.25 * pow(max(dot(n, normalize(sun + eye)), 0.0), 40.0) * lit;

    // the air: in-scatter and the light left (turns the day / night line orange)
    vec3 air_col = 1.0 - exp(-(v_in + 0.25 * v_left));
    surf = surf * air_col.b + surf * (1.0 - density) + air_col * density;
    surf += clamp(1.0 - lum(surf * 6.0), 0.0, 1.0) * glow * (1.0 - ice);   // city lights only in the dark

    // clouds and haze: the haze is thicker towards the rim
    float rim = 1.0 - abs(dot(eye, n));
    vec4 haze = atmo;
    haze.a += pow(rim, 6.0) * pow(atmo.a, 0.3) - (1.0 - pow(rim, 0.2)) * atmo.a * 0.5;
    if (is_moon) haze.a += pow(clamp(1.0 - abs(dot(sun, n)), 0.0, 1.0), 6.0) * 0.075;
    vec4 cl = has_clouds ? clouds(UV2, t, haze) : haze;
    // cloud shadow: a cloud a little towards the sun darkens the ground
    float shade = 1.0;
    if (has_clouds) {
        if (cl.a > 0.85) shade -= (1.0 - dot(sun, n)) * 0.65 * cl.a;
        float a2 = clouds(UV2 + vec2(0.002, 0.001), t, vec4(haze.rgb, 0.0)).a;
        if (a2 > 0.25) shade -= mix(1.0, 0.05, dot(sun, n)) * 0.75 * a2;
        shade = max(shade, 0.0);
    }
    cl.rgb = clamp(screen(cl.rgb, haze.rgb), 0.0, 1.0);
    cl.rgb = is_moon ? cl.rgb + atmo.rgb : mix(cl.rgb, atmo.rgb, clamp(cloud_tint, 0.0, 1.0));
    surf = mix(surf * shade, cl.rgb * 2.0 * air_col + air_col, clamp(cl.a, 0.0, 1.0));

    // X4's HDR tonemapping keeps the bright limb and clouds below white (game: about 200-220 of 255): soft shoulder
    // above knee, levelling off at knee + shoulder
    vec3 over = max(surf - knee, vec3(0.0));
    surf = min(surf, vec3(knee)) + shoulder * (1.0 - exp(-over / shoulder));
    // computed on stored (sRGB) values: to linear light
    ALBEDO = pow(clamp(surf, 0.0, 1.0), vec3(2.2));
}
";

    // ---- the atmosphere shell: drawn from its inside faces (the far side), added to what is behind
    const string ShellCode = "shader_type spatial;\nrender_mode unshaded, blend_add, cull_front, depth_draw_never, fog_disabled;\n" + Common + @"
uniform vec3 colour = vec3(1.0);   // color_Atmo; when its red is below 1 it replaces the computed colour
uniform float density = 1.0;
varying vec3 v_ray;                // Rayleigh light
varying vec3 v_eye;                // towards the camera (planet space)

void vertex() {
    vec3 p, cam;
    planet_space(MODEL_MATRIX, VERTEX, INV_VIEW_MATRIX, p, cam);
    float top = r_outer / r_inner;
    float thick = 1.0 / (top - 1.0);
    vec3 sun = normalize(x4_sun_dir);
    vec3 ray = p - cam;
    float far = length(ray);
    ray /= far;
    float near = enter(cam, ray, top);
    vec3 start = cam + ray * near;
    float len = (far - near) / 3.0;               // three samples
    float start_air = exp(-1.0 / AIR_DEPTH) * air(dot(ray, start) / top);
    vec3 loss = inv_wave4 * KR * PI4 + KM * PI4;
    vec3 sum = vec3(0.0);
    for (int i = 0; i < 3; i++) {
        vec3 q = start + ray * len * (float(i) + 0.5);
        float hq = length(q);
        float d = exp((1.0 - hq) * thick / AIR_DEPTH);
        float sc = start_air + d * (air(dot(sun, q) / hq) - air(dot(ray, q) / hq));
        sum += clamp(exp(-sc * loss), 0.0, 1.0) * d * len * thick;
    }
    v_ray = sum * inv_wave4 * KR * esun;
    v_eye = cam - p;
}

void fragment() {
    float c = dot(normalize(x4_sun_dir), normalize(v_eye));
    vec3 col = (0.75 + 0.75 * c * c) * v_ray;           // Rayleigh phase
    col = (1.0 - exp(-4.0 * col)) * density;            // exposure
    if (colour.r < 1.0) col = colour * col.b * col.b;
    ALBEDO = clamp(col, 0.0, 1.0);
}
";

    /// <summary>Look checks: X4ED_PLANET=light=1.2,ambient=3 overrides those uniforms (unknown names are ignored).</summary>
    static void Tune(ShaderMaterial m)
    {
        foreach (var kv in (System.Environment.GetEnvironmentVariable("X4ED_PLANET") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (kv.Split('=') is { Length: 2 } p && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
                && m.Shader.GetShaderUniformList().Any(u => (string)u.AsGodotDictionary()["name"] == p[0]))
                m.SetShaderParameter(p[0], v);
    }

    static Shader? _surface, _shell;
    static Shader S(ref Shader? s, string code) { Globals(); return s ??= new Shader { Code = code }; }

    /// <summary>True for X4's planet surface and atmosphere shaders handled here.</summary>
    public static bool Handles(MaterialLibrary.Material x) =>
        x.Shader is "p1_planet_earthlike" or "p1_planet_earthlike_tes" or "p1_planet_moon" or "p1_planet_atmosphere_scattering"
        || x.BlendMode == "PLANET_ATMOSPHERE";

    /// <summary>The Godot material for a planet material; null when it is none.</summary>
    public static Material? Build(MaterialLibrary.Material x, Func<string, Texture2D?> tex)
    {
        if (!Handles(x)) return null;
        Texture2D? T(string n) => x.Map(n) is { } p ? tex(p) : null;
        float F(string n, float f) => x.Float(n, f);
        Vector3 C(string n, Vector3 f) => x.Color(n) is { } c ? new Vector3(c.R, c.G, c.B) : f;
        Vector2 Tile(string layer, float f) => new(F(layer + "_tiling", f), F(layer + "_tiling_v", F(layer + "_tiling", f)));
        Vector3 InvWave4() => new(1f / MathF.Pow(F("wavelength_red", 0.65f), 4), 1f / MathF.Pow(F("wavelength_green", 0.57f), 4), 1f / MathF.Pow(F("wavelength_blue", 0.475f), 4));
        void Radii(ShaderMaterial m, float esunDefault)
        {
            float inner = F("fInnerRadius", 1000f), outer = F("fOuterRadius", inner * 1.025f);
            m.SetShaderParameter("r_inner", inner);
            m.SetShaderParameter("r_outer", outer > inner ? outer : inner * 1.025f);
            m.SetShaderParameter("esun", F("m_ESun", esunDefault));
            m.SetShaderParameter("inv_wave4", InvWave4());
        }

        if (x.Shader == "p1_planet_atmosphere_scattering" || x.BlendMode == "PLANET_ATMOSPHERE")
        {
            var m = new ShaderMaterial { Shader = S(ref _shell, ShellCode) };
            Radii(m, 4f);
            m.SetShaderParameter("colour", C("color_Atmo", Vector3.One));
            m.SetShaderParameter("density", F("fDensity", 1f));
            Tune(m);
            return m;
        }

        bool moon = x.Shader == "p1_planet_moon";
        var s = new ShaderMaterial { Shader = S(ref _surface, SurfaceCode) };
        void Set(string uniform, Texture2D? t, string? flag = null) { if (t == null) return; s.SetShaderParameter(uniform, t); if (flag != null) s.SetShaderParameter(flag, true); }
        Radii(s, 2f);
        s.SetShaderParameter("is_moon", moon);
        Set("height_map", T("diffuse_map"));
        s.SetShaderParameter("height_str", F("diffuseStr", 1f));
        s.SetShaderParameter("offset_u", F("DiffOffsetU", 0f));
        Set("detail_map", T("diffuse_detail_map"), "has_detail");
        s.SetShaderParameter("detail_tiling", Tile("diffuse_detail", 3f));
        s.SetShaderParameter("detail_str", F("diffuse_detailStr", 1f));
        s.SetShaderParameter("detail_offset", new Vector2(F("DiffDetailOffsetU", 0f), F("DiffDetailOffsetV", 0f)));
        Set("polar_map", T("polar_map"), "has_polar");
        s.SetShaderParameter("polar_scale", F("PolarScale", 0.5f));
        s.SetShaderParameter("ice_col_scale", F("IceColScale", 0f));
        s.SetShaderParameter("water", C("color_water", new Vector3(0.5f, 0, 0)));
        s.SetShaderParameter("plains", C("color_plains", new Vector3(0.5f, 0, 0)));
        s.SetShaderParameter("water_level", F("waterlevel", 0.15f));
        Set("height_grad", T("color_height_map"), "has_height_grad");
        if (!moon) Set("climate_grad", T("color_climate_map"), "has_climate");
        if (x.Map("citymap_map") != null) Set("glow_map", T("color_glow_map"), "has_glow");
        s.SetShaderParameter("city_tiling", Tile("citymap", 1f));
        s.SetShaderParameter("glow_str", F("color_glowStr", 1f));
        s.SetShaderParameter("city_level", F("citylevel", 0.15f));
        Set("normal_map", T("normal_map"), "has_normal");
        s.SetShaderParameter("normal_str", F("normalStr", 1f));
        Set("normal_detail_map", T("normal_detail_map"), "has_normal_detail");
        s.SetShaderParameter("normal_detail_tiling", Tile("normal_detail", 4f));
        s.SetShaderParameter("normal_detail_str", F("normal_detailStr", 1f));

        // clouds: earthlike planets take their cover from cloudmap_map, moons from clouds_mask_map
        var mask = moon ? T("clouds_mask_map") : T("cloudmap_map");
        float maskStr = moon ? F("clouds_maskStr", 1f) : F("cloudmapStr", 1.3f);
        var cloudTex = T("cloudmap_map");
        if (mask != null && cloudTex != null && maskStr > 0f)
        {
            Set("cloud_mask", mask, "has_clouds");
            Set("cloud_tex", cloudTex);
            Set("dense_tex", T("densecloudmap_map") ?? cloudTex);
        }
        s.SetShaderParameter("cloud_mask_str", maskStr);
        Set("cloud_detail_mask", T("clouds_detail_mask_map"), "has_cloud_detail");
        s.SetShaderParameter("cloud_detail_tiling", Tile("clouds_detail_mask", 3f));
        s.SetShaderParameter("cloud_detail_str", F("clouds_detail_maskStr", 1.3f));
        s.SetShaderParameter("cloud_offset", new Vector2(F("CloudsDiffOffsetU", 0f), 0f));
        s.SetShaderParameter("cloud_detail_offset", new Vector2(F("CloudsDiffDetailOffsetU", 0f), F("CloudsDiffDetailOffsetV", 0f)));
        s.SetShaderParameter("cloud_cut", F("cloudCutOff", 0.2f));
        s.SetShaderParameter("dense_cut", F("denseCutOff", 0.5f));
        s.SetShaderParameter("cloud_speed", F("cloud_speed", 0.0012f));
        s.SetShaderParameter("cloud_tint", F("cloud_colorfactor", 0f));
        var a = C("color_atmo", new Vector3(1, 1, 1));
        s.SetShaderParameter("atmo", new Vector4(a.X, a.Y, a.Z, x.Alpha("color_atmo", 1f)));
        s.SetShaderParameter("density", F("fDensity", 1f));
        s.SetShaderParameter("hue", F("base_hue_shift", 0f));
        s.SetShaderParameter("sat", F("base_saturation_shift", 1f));
        s.SetShaderParameter("bright", F("base_brightness_shift", 1f));
        s.SetShaderParameter("contrast", F("base_contrast_shift", 1f));
        Tune(s);
        return s;
    }
}
