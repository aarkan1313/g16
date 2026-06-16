shader_type spatial;

// TERRAIN LOOK LAB — 7 material zones (valley, valley/slope transition, slope,
// slope/cliff transition, cliff, high, peak) composited by a SWAPPABLE mask mode
// and rendered with a SWAPPABLE blend/shader mode. Each zone has its own PBR set.
// All driven from the on-screen panel. Surface-only materials; flora is a future
// decoration pass.

uniform sampler2D heightmap : repeat_disable, filter_linear;
uniform float region_size = 8192.0;
uniform float texel_world = 4.0;

// 7 zone materials, each albedo + normal + roughness + ao.
uniform sampler2D z0_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z0_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z0_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z1_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z1_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z1_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z2_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z2_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z2_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z3_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z3_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z3_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z4_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z4_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z4_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z5_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z5_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z5_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z6_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z6_nrm : hint_normal, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D z6_rgh : hint_roughness_gray, filter_linear_mipmap_anisotropic, repeat_enable;

uniform float tex_scale_m = 9.0;

// mask_mode: 0 height bands | 1 height+slope | 2 noise-broken bands |
//            3 curvature-aware (dirt in hollows) | 4 steep=cliff sharp |
//            5 world-noise biome patches
uniform int mask_mode = 2;
// blend_mode: 0 flat albedo | 1 full PBR | 2 PBR + anti-tile |
//             3 PBR + anti-tile + macro-tint | 4 + distance-detail (full stack)
uniform int blend_mode = 3;

// Zone height/slope breakpoints (meters / 0..1 slope). 7 zones need 6 splits.
uniform float h_valley = 100.0;
uniform float h_slope = 350.0;
uniform float h_high = 700.0;
uniform float h_peak = 950.0;
uniform float slope_cliff_lo = 0.30;
uniform float slope_cliff_hi = 0.55;
uniform float band_softness_m = 120.0;
uniform float edge_noise_m = 80.0;
uniform float edge_noise_amp = 0.30;
uniform float macro_m = 480.0;
uniform float macro_amp = 0.14;
uniform float antitile_mult = 5.3;

varying vec2 v_uv;
varying vec3 v_world;
varying vec3 v_normal;
varying float v_h;
varying float v_slope;
varying float v_curv;

float height_at(vec2 uv) { return texture(heightmap, uv).r; }
float hash2(vec2 p){ return fract(sin(dot(p,vec2(127.1,311.7)))*43758.5453); }
float vnoise(vec2 w,float wl){ vec2 p=w/wl; vec2 i=floor(p); vec2 f=fract(p); f=f*f*(3.0-2.0*f);
    return mix(mix(hash2(i),hash2(i+vec2(1,0)),f.x),mix(hash2(i+vec2(0,1)),hash2(i+vec2(1,1)),f.x),f.y); }

vec3 s_alb(sampler2D t, vec3 wp, vec3 nr){
    // triplanar anti-tiled albedo
    vec3 bw=pow(abs(nr),vec3(4.0)); bw/=(bw.x+bw.y+bw.z);
    vec2 ux=wp.zy/tex_scale_m, uy=wp.xz/tex_scale_m, uz=wp.xy/tex_scale_m;
    vec3 base = texture(t,ux).rgb*bw.x + texture(t,uy).rgb*bw.y + texture(t,uz).rgb*bw.z;
    if (blend_mode >= 2) {
        vec3 big = texture(t,ux/antitile_mult).rgb*bw.x + texture(t,uy/antitile_mult).rgb*bw.y + texture(t,uz/antitile_mult).rgb*bw.z;
        base = mix(base, big, 0.5);
    }
    return base;
}
vec3 s_nrm(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=pow(abs(nr),vec3(4.0)); bw/=(bw.x+bw.y+bw.z);
    return texture(t,wp.zy/tex_scale_m).rgb*bw.x + texture(t,wp.xz/tex_scale_m).rgb*bw.y + texture(t,wp.xy/tex_scale_m).rgb*bw.z;
}
float s_rgh(sampler2D t, vec3 wp, vec3 nr){
    vec3 bw=pow(abs(nr),vec3(4.0)); bw/=(bw.x+bw.y+bw.z);
    return texture(t,wp.zy/tex_scale_m).r*bw.x + texture(t,wp.xz/tex_scale_m).r*bw.y + texture(t,wp.xy/tex_scale_m).r*bw.z;
}

void vertex(){
    v_uv=(VERTEX.xz+vec2(region_size*0.5))/region_size;
    VERTEX.y=height_at(v_uv);
    v_world=VERTEX;
    float t=texel_world/region_size;
    float hl=height_at(v_uv-vec2(t,0)), hr=height_at(v_uv+vec2(t,0));
    float hd=height_at(v_uv-vec2(0,t)), hu=height_at(v_uv+vec2(0,t));
    v_normal=normalize(vec3(hl-hr,2.0*texel_world,hd-hu));
    v_h=VERTEX.y; v_slope=1.0-v_normal.y;
    v_curv=(hl+hr+hd+hu)*0.25 - VERTEX.y;
}

// Returns the 7 zone weights given height, slope, curvature + the chosen mask.
void zone_weights(out float w[7], float h, float slope, float curv, vec3 wp){
    for(int i=0;i<7;i++) w[i]=0.0;
    float en = (mask_mode==2 || mask_mode==3) ? (vnoise(wp.xz,edge_noise_m)-0.5)*2.0*edge_noise_amp : 0.0;
    float hj = h + en*250.0;
    float sj = slope + en*0.12;
    float soft = band_softness_m;

    if (mask_mode==5){
        // world-noise biome patches: large blobs pick a dominant zone, still
        // modulated by height so peaks stay snowy.
        float b = vnoise(wp.xz, macro_m*1.4);
        int pick = int(clamp(floor(b*5.0),0.0,4.0));   // 0..4 of the ground zones
        w[pick]=1.0;
        // cliffs/peaks override by slope/height regardless of biome.
        float cliff=smoothstep(slope_cliff_lo,slope_cliff_hi,sj);
        float peak=smoothstep(h_peak-soft,h_peak+soft,hj)*(1.0-smoothstep(0.35,0.6,slope));
        for(int i=0;i<7;i++) w[i]*=(1.0-max(cliff,peak));
        w[4]+=cliff; w[6]+=peak;
        return;
    }

    // Height-band weights (valley, lowtrans, slope, hightrans, high) with soft edges.
    float v   = 1.0 - smoothstep(h_valley-soft, h_valley+soft, hj);
    float hi  = smoothstep(h_high-soft, h_high+soft, hj);
    float mid = clamp(1.0 - v - hi, 0.0, 1.0);
    float lowtrans = mid * (1.0 - smoothstep(h_slope-soft, h_slope+soft, hj));
    float slopez   = mid - lowtrans;

    w[0]=v; w[1]=lowtrans; w[2]=slopez; w[3]=0.0; w[5]=hi;

    // Slope -> cliff (zones 3 transition, 4 cliff). Stronger when mask uses slope.
    float useSlope = (mask_mode==1||mask_mode==2||mask_mode==4) ? 1.0 : 0.6;
    float cliff = smoothstep(slope_cliff_lo, slope_cliff_hi, sj) * useSlope;
    float cliffTrans = smoothstep(slope_cliff_lo*0.6, slope_cliff_lo, sj) * (1.0-cliff) * useSlope;
    // Curvature: hollows (curv>0) bias toward the transition/dirt zone 1.
    if (mask_mode==3){ float hollow=smoothstep(0.5,2.5,curv); w[1]+=hollow*0.5; }
    // Apply cliff over the ground zones.
    float groundScale=1.0-max(cliff,cliffTrans);
    for(int i=0;i<6;i++) if(i!=3&&i!=4) w[i]*=groundScale;
    w[3]+=cliffTrans; w[4]+=cliff;

    // Peak/snow on high flat ground.
    float peak=smoothstep(h_peak-soft,h_peak+soft,hj)*(1.0-smoothstep(0.35,0.6,slope));
    for(int i=0;i<6;i++) w[i]*=(1.0-peak);
    w[6]=peak;

    // normalize
    float sum=0.0; for(int i=0;i<7;i++) sum+=w[i];
    if(sum>1e-4) for(int i=0;i<7;i++) w[i]/=sum;
}

void fragment(){
    vec3 wp=v_world; vec3 nr=v_normal;
    float w[7]; zone_weights(w, v_h, v_slope, v_curv, wp);

    vec3 alb=vec3(0.0); vec3 nrm=vec3(0.0,0.0,1.0); float rgh=0.0;
    // accumulate weighted PBR across zones
    #define ZONE(i,A,N,R) alb+=w[i]*s_alb(A,wp,nr); if(blend_mode>=1){ nrm+=w[i]*(s_nrm(N,wp,nr)*2.0-1.0); rgh+=w[i]*s_rgh(R,wp,nr);} else rgh+=w[i]*0.9;
    ZONE(0,z0_alb,z0_nrm,z0_rgh)
    ZONE(1,z1_alb,z1_nrm,z1_rgh)
    ZONE(2,z2_alb,z2_nrm,z2_rgh)
    ZONE(3,z3_alb,z3_nrm,z3_rgh)
    ZONE(4,z4_alb,z4_nrm,z4_rgh)
    ZONE(5,z5_alb,z5_nrm,z5_rgh)
    ZONE(6,z6_alb,z6_nrm,z6_rgh)

    // macro tint (broad value drift).
    if (blend_mode>=3){ float m=vnoise(wp.xz,macro_m); alb*=(1.0+(m-0.5)*2.0*macro_amp); }

    ALBEDO=alb;
    ROUGHNESS=clamp(rgh,0.04,1.0);
    if (blend_mode>=1){
        // blend tangent-space normals back to world-ish via the geometric normal.
        vec3 wn=normalize(nr + vec3(nrm.x,0.0,nrm.y)*0.8);
        NORMAL=normalize((VIEW_MATRIX*vec4(wn,0.0)).xyz);
    } else {
        NORMAL=normalize((VIEW_MATRIX*vec4(nr,0.0)).xyz);
    }
}
