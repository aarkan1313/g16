shader_type sky;

// Volumetric cloud sky (Stage 3). Raymarches a Perlin-Worley density field in a
// spherical shell above the world and composites it into the sky by view direction
// (EYEDIR). Technique referenced from HZD/Nubis (Schneider/Guerrilla) + the clayjohn
// Godot pattern — our own implementation. One job: turn the baked noise + weather +
// sun into sky radiance. Knows nothing about the terrain (the matched GROUND shadow
// is a separate pass, Stage 5). Frame-amortization/reduced-res come from the Sky
// resource's ProcessMode/RadianceSize (set in C#), not from this shader.

// --- baked inputs (bound from CloudVolume) ----------------------------------
uniform sampler3D shape_tex : filter_linear, repeat_enable;   // R=Perlin-Worley, GBA=Worley octaves
uniform sampler3D detail_tex : filter_linear, repeat_enable;  // R=high-freq erosion
uniform sampler2D weather_tex : filter_linear, repeat_enable; // R=coverage, G=type

// --- live knobs (pushed from the Clouds tab; names match PushSky uniforms) ----
uniform bool cloud_enabled = true;
uniform float cloud_coverage : hint_range(0.0, 1.0) = 0.35;
uniform float cloud_density : hint_range(0.0, 3.0) = 1.0;
uniform float cloud_type : hint_range(0.0, 1.0) = 0.6;       // 0 stratus → 1 cumulus
uniform float cloud_altitude = 1800.0;                       // base height (m)
uniform float cloud_thickness = 1400.0;                      // shell vertical extent (m)
uniform float cloud_drift_speed = 12.0;
uniform float cloud_drift_dir = 45.0;
uniform float cloud_hg : hint_range(0.0, 0.95) = 0.6;        // HG forward scatter
uniform float cloud_powder : hint_range(0.0, 2.0) = 1.0;
uniform float cloud_sun_absorb : hint_range(0.1, 2.0) = 0.75;
uniform int cloud_steps : hint_range(16, 160) = 96;

// --- shape / size / opacity knobs -------------------------------------------
uniform float cloud_size : hint_range(0.3, 4.0) = 1.0;       // overall feature size (×)
uniform float cloud_detail : hint_range(0.0, 1.0) = 0.4;     // high-freq edge erosion amount
uniform float cloud_detail_size : hint_range(0.3, 4.0) = 1.0;// size of the wispy detail (×)
uniform float cloud_edge : hint_range(0.0, 1.0) = 0.5;       // shape hardness (0 soft↔1 crisp)
uniform float cloud_opacity : hint_range(0.2, 4.0) = 1.0;    // extinction (translucent↔solid)
uniform float cloud_brightness : hint_range(0.2, 3.0) = 1.0; // overall cloud lightness
uniform float cloud_ambient : hint_range(0.0, 2.0) = 0.7;    // sky-fill on shadowed sides

// debug: 0 normal | 1 shell magenta | 2 density | 3 raw shape.r | 4 weather.r
uniform int cloud_debug = 0;

uniform vec3 cloud_tint : source_color = vec3(1.0, 1.0, 1.0);
uniform float weather_scale = 0.00008;   // world→weather UV (≈ patch size over the map)
uniform float shape_scale = 0.0006;      // world→shape-volume scale (≈1.6km features)
uniform float detail_scale = 0.004;      // world→detail-volume scale

// Background sky gradient (mirrors the procedural sky we replace, passed from C#
// so the underlying sky look is preserved; clouds composite OVER this).
uniform vec3 sky_top : source_color = vec3(0.30, 0.48, 0.74);
uniform vec3 sky_horizon : source_color = vec3(0.68, 0.74, 0.80);
uniform vec3 ground_color : source_color = vec3(0.22, 0.26, 0.22);

vec3 background(vec3 dir){
    if (dir.y >= 0.0){
        float t = pow(clamp(dir.y, 0.0, 1.0), 0.5);
        return mix(sky_horizon, sky_top, t);
    }
    return mix(sky_horizon, ground_color, clamp(-dir.y * 4.0, 0.0, 1.0));
}

// Shell radii: a large planet so the layer reads flat-ish near the camera, like sky.
const float PLANET_R = 200000.0;

float remap(float v, float a, float b, float c, float d){
    return c + (v - a) * (d - c) / max(b - a, 1e-5);
}

// height fraction 0 (base) .. 1 (top) within the shell
float height_fraction(vec3 p, float baseR, float topR){
    return clamp((length(p) - baseR) / max(topR - baseR, 1.0), 0.0, 1.0);
}

// cloud-type vertical density gradient (stratus low/flat → cumulus tall/puffy).
// SOFT round-off at base + top so clouds have body through the shell middle (the
// old narrow-band version zeroed density across most of the shell). h in [0,1].
float type_gradient(float h, float type){
    float baseRound = smoothstep(0.0, 0.15, h);        // fade in off the floor
    float topFade = 1.0 - smoothstep(mix(0.5, 0.95, type), 1.0, h); // taller for cumulus
    return baseRound * topFade;
}

// density at world point p within shell [baseR,topR]. Coordinates are taken
// RELATIVE to the shell (p - shell origin) before scaling, so feature size is set
// by *_scale in meters rather than by absolute planet coords (which made the base
// shape high-frequency and near-zero — the Stage-3 "no clouds" bug).
float sample_density(vec3 p, float baseR, float topR, vec2 windOff){
    float h = height_fraction(p, baseR, topR);
    // local horizontal position on the shell (origin under the camera)
    vec3 lp = vec3(p.x, p.y - baseR, p.z);

    // weather: coverage (R) + type (G)
    vec2 wuv = lp.xz * weather_scale + windOff * weather_scale;
    vec4 w = texture(weather_tex, wuv);
    float coverage = clamp(cloud_coverage + (w.r - 0.5) * 0.6, 0.0, 1.0);
    float type = clamp(cloud_type + (w.g - 0.5) * 0.4, 0.0, 1.0);

    // base shape: sh.r is already Perlin-Worley; lightly erode with the Worley FBM.
    // cloud_size scales features (bigger size = lower sample frequency).
    float sScale = shape_scale / cloud_size;
    vec3 suv = lp * sScale + vec3(windOff.x, h, windOff.y) * sScale;
    vec4 sh = texture(shape_tex, suv);
    float fbm = sh.g * 0.625 + sh.b * 0.25 + sh.a * 0.125;
    float base = remap(sh.r, fbm * 0.3, 1.0, 0.0, 1.0);     // gentle erosion, keeps body

    // coverage carves how much sky is clouded. cloud_edge sets the transition
    // hardness: soft (wide remap band) ↔ crisp (narrow band near the threshold).
    float band = mix(0.5, 0.02, cloud_edge);
    float lo = clamp(1.0 - coverage, 0.0, 1.0);
    base = remap(base, lo, min(lo + band, 1.0), 0.0, 1.0);
    base = clamp(base, 0.0, 1.0);

    // vertical cloud-type gradient (soft, so the shell middle has body)
    base *= type_gradient(h, type);

    // erode edges with high-freq detail (amount + size are knobs)
    if (base > 0.0 && cloud_detail > 0.0){
        float dScale = detail_scale / cloud_detail_size;
        vec3 duv = lp * dScale + vec3(windOff.x, h, windOff.y) * dScale;
        float det = texture(detail_tex, duv).r;
        base = remap(base, det * 0.6 * cloud_detail, 1.0, 0.0, 1.0);
        base = clamp(base, 0.0, 1.0);
    }

    return base * cloud_density;
}

// Henyey-Greenstein phase
float hg(float cosA, float g){
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * 3.14159265 * pow(1.0 + g2 - 2.0 * g * cosA, 1.5));
}

// short light-cone march toward the sun → Beer transmittance to the light
float light_march(vec3 p, vec3 L, float baseR, float topR, vec2 windOff){
    const int LSTEPS = 6;
    float lss = cloud_thickness / float(LSTEPS) * 0.5;
    float d = 0.0;
    vec3 q = p;
    for (int i = 0; i < LSTEPS; i++){
        q += L * lss;
        d += sample_density(q, baseR, topR, windOff) * lss;
    }
    // one distant sample to catch far occlusion
    d += sample_density(p + L * lss * 18.0, baseR, topR, windOff) * lss;
    return exp(-d * cloud_sun_absorb * 0.02);   // same 1/m scale as view extinction
}

// ray vs sphere (returns near/far t in .x/.y; .x<0 if behind)
vec2 ray_sphere(vec3 ro, vec3 rd, float R){
    float b = dot(ro, rd);
    float c = dot(ro, ro) - R * R;
    float disc = b * b - c;
    if (disc < 0.0) return vec2(-1.0);
    float s = sqrt(disc);
    return vec2(-b - s, -b + s);
}

void sky(){
    // NOTE: Godot's sky() processor forbids `return` (like fragment()). So the whole
    // function computes into `outColor` and writes once at the end — guarded by
    // `doMarch`, never an early return.
    vec3 rd = EYEDIR;
    vec3 bg = background(rd);
    vec3 outColor = bg;

    vec3 ro = vec3(0.0, PLANET_R + 1.0, 0.0);
    float baseR = PLANET_R + cloud_altitude;
    float topR = baseR + cloud_thickness;
    vec2 hitB = ray_sphere(ro, rd, baseR);
    vec2 hitT = ray_sphere(ro, rd, topR);
    float tStart = max(hitB.y, 0.0);
    float tEnd = max(hitT.y, 0.0);

    // march only when clouds on, looking up, and the shell is actually crossed
    bool doMarch = cloud_enabled && rd.y > 0.02 && tEnd > tStart;
    if (doMarch){
        int steps = clamp(cloud_steps, 16, 160);
        float dt = (tEnd - tStart) / float(steps);

        float ang = radians(cloud_drift_dir);
        vec2 windOff = vec2(cos(ang), sin(ang)) * TIME * cloud_drift_speed;

        vec3 L = normalize(LIGHT0_DIRECTION);
        float cosA = dot(rd, L);
        float phase = mix(hg(cosA, cloud_hg), hg(cosA, -0.2), 0.3);

        vec3 sunCol = LIGHT0_COLOR * LIGHT0_ENERGY;
        vec3 ambient = mix(vec3(0.4, 0.45, 0.55), vec3(0.9, 0.93, 1.0), 0.5);

        float T = 1.0;
        vec3 scattered = vec3(0.0);
        float maxDens = 0.0;
        float t = tStart;
        for (int i = 0; i < steps; i++){
            vec3 p = ro + rd * t;
            float dens = sample_density(p, baseR, topR, windOff);
            maxDens = max(maxDens, dens);
            if (dens > 0.001){
                float lightT = light_march(p, L, baseR, topR, windOff);
                // powder darkens edges but mustn't kill the body — keep a floor.
                float powder = mix(1.0, 1.0 - exp(-dens * 2.0 * cloud_powder), 0.5);
                // extinction in 1/m (cloud_opacity = translucent↔solid)
                float sigma = dens * 0.02 * cloud_opacity;
                float beer = exp(-sigma * dt);
                // direct sun (phase-shaped) + a multiple-scatter floor so shadowed
                // sides read as bright cloud, not black; cloud_ambient fills the rest.
                float sun = lightT * (phase + 0.4);
                vec3 lum = (sunCol * sun + ambient * cloud_ambient) * cloud_brightness;
                lum *= powder;
                // energy-conserving integration of in-scattering over the step
                scattered += T * lum * (1.0 - beer);
                T *= beer;
                if (T < 0.01) { break; }   // break is allowed; return is not
            }
            t += dt;
        }
        float cloudAlpha = clamp(1.0 - T, 0.0, 1.0);
        outColor = mix(bg, scattered * cloud_tint, cloudAlpha);

        if (cloud_debug == 1){ outColor = vec3(1.0, 0.0, 1.0); }              // shell marched here
        else if (cloud_debug == 2){ outColor = vec3(clamp(maxDens, 0.0, 1.0)); } // post-remap density
        else if (cloud_debug == 3){                                            // RAW shape.r at shell mid
            vec3 pm = ro + rd * (tStart + (tEnd - tStart) * 0.5);
            vec3 lp = vec3(pm.x, pm.y - baseR, pm.z);
            outColor = texture(shape_tex, lp * shape_scale).rrr;
        }
        else if (cloud_debug == 4){                                            // weather.r
            vec3 pm = ro + rd * (tStart + (tEnd - tStart) * 0.5);
            outColor = texture(weather_tex, pm.xz * weather_scale).rrr;
        }
    }

    COLOR = outColor;
    ALPHA = 1.0;
}
