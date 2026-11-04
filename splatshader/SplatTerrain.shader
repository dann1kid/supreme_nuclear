// SplatTerrain — тайловый сплат-материал рельефа Supreme Nuclear.
// Канон SupCom/Neroxis: маска видов поверхности (на всю карту) + ТАЙЛОВЫЕ
// детальные текстуры (период метры) + макро-зоны (наш albedo как hue).
// Всё сэмплится ГЛОБАЛЬНЫМИ координатами: world = global + origin.position
// (GlobalPositionExtensions.ToListPosition) ⇒ global = world − origin.
// Глобал _Datum_OriginPosition ставит ShaderGlobalManager.SetDatum при
// каждом сдвиге FloatingOrigin — сэмплинг стабилен, меш может жить без UV
// (как у стокового Shader Graphs/Field).
//
// Сборка: Unity Editor 2022.3.x + URP 14 (README рядом) — бандл
// BepInEx/plugins/SupremeNuclear/splat.bundle, загрузчик TerrainSplat.cs.
// Фолбэк мода при отсутствии бандла — URP Lit-гибрид.
Shader "SupremeNuclear/SplatTerrain"
{
    Properties
    {
        [Header(Macro zones)] [NoScaleOffset] _MacroMap ("Macro albedo (zones, whole map)", 2D) = "white" {}
        _MacroScale ("Macro world size (m)", Float) = 81920
        [Header(Surface mask)] [NoScaleOffset] _MaskMap ("Splat mask RGBA (grass sand rock snow)", 2D) = "red" {}
        [Header(Tiled detail)] [NoScaleOffset] _Tile0 ("Tile 0 grass", 2D) = "white" {}
        [NoScaleOffset] _Tile1 ("Tile 1 sand", 2D) = "white" {}
        [NoScaleOffset] _Tile2 ("Tile 2 rock", 2D) = "white" {}
        [NoScaleOffset] _Tile3 ("Tile 3 snow", 2D) = "white" {}
        _TileMetres ("Tile period (m)", Float) = 4
        _DetailStrength ("Detail strength", Range(0, 2)) = 1
        [Header(Normal detail)] [NoScaleOffset] _BumpMap ("Detail normal", 2D) = "bump" {}
        _BumpMetres ("Normal period (m)", Float) = 1
        _BumpScale ("Normal scale", Range(0, 2)) = 1

        // --- URP-обязательные (SRP-батчер/матч-фильтры движка) ---
        _BaseColor ("Color", Color) = (1, 1, 1, 1)
        _BaseMap ("BaseMap (unused)", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
        _Smoothness ("Smoothness", Range(0, 1)) = 0.0
        [Enum(Off,0,On,1)] _Surface ("Surface", Float) = 0
        [HideInInspector] _DstBlend ("Dst Blend", Float) = 0
        [HideInInspector] _SrcBlend ("Src Blend", Float) = 1
        [HideInInspector] _ZWrite ("ZWrite", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry-1"
            "IgnoreProjector" = "True"
        }
        LOD 300

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        // LerpWhiteTo (используется Shadows.hlsl) объявлен здесь — стандартные
        // шейдеры тянут его через Input.hlsl, кастомный обязан сам.
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"

        TEXTURE2D(_MacroMap);   SAMPLER(sampler_MacroMap);
        TEXTURE2D(_MaskMap);    SAMPLER(sampler_MaskMap);
        TEXTURE2D(_Tile0);      SAMPLER(sampler_Tile0);
        TEXTURE2D(_Tile1);      SAMPLER(sampler_Tile1);
        TEXTURE2D(_Tile2);      SAMPLER(sampler_Tile2);
        TEXTURE2D(_Tile3);      SAMPLER(sampler_Tile3);
        TEXTURE2D(_BumpMap);    SAMPLER(sampler_BumpMap);

        CBUFFER_START(UnityPerMaterial)
            float _MacroScale;
            float _TileMetres;
            float _DetailStrength;
            float _BumpMetres;
            float _BumpScale;
            half4 _BaseColor;
            half _Cutoff;
            half _Smoothness;
            float _Surface;
            float _DstBlend;
            float _SrcBlend;
            float _ZWrite;
        CBUFFER_END

        // Глобал игры (ShaderGlobalManager.SetDatum): позиция Datum.origin.
        float4 _Datum_OriginPosition;
        ENDHLSL

        // ---------- ForwardLit ----------
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]

            HLSLPROGRAM
            #pragma vertex SplatVert
            #pragma fragment SplatFrag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            // КАНОН URP 14: мягкие тени — ЧЕТЫРЕ варианта качества; без них
            // при _SHADOWS_SOFT_MEDIUM остаёмся с жёсткой выборкой — тёмные
            // пятна на близких каскадах (плейтест 2026-11-03).
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float4 tangentOS  : TANGENT;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float4 tangentWS  : TEXCOORD2;
                float  fogFactor  : TEXCOORD3;
            };

            Varyings SplatVert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                VertexPositionInputs posIn = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs nrmIn = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.positionCS = posIn.positionCS;
                output.positionWS = posIn.positionWS;
                output.normalWS = nrmIn.normalWS;
                output.tangentWS = float4(nrmIn.tangentWS, input.tangentOS.w * GetOddNegativeScale());
                output.fogFactor = ComputeFogFactor(posIn.positionCS.z);
                return output;
            }

            half4 SplatFrag(Varyings input) : SV_Target
            {
                // Глобальная XZ фрагмента: world − origin (см. шапку).
                float2 g = input.positionWS.xz - _Datum_OriginPosition.xz;

                // Маска видов + макро-hue — на всю карту (0..1 по габариту).
                float2 uvMap = g / _MacroScale + 0.5;
                half4 w = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, uvMap);
                w /= max(dot(w, 1.0h.xxxx), 1e-3h);
                half3 macro = SAMPLE_TEXTURE2D(_MacroMap, sampler_MacroMap, uvMap).rgb;

                // Тайлы — период _TileMetres по глобальным метрам.
                float2 uvTile = g / _TileMetres;
                half d =
                    w.r * SAMPLE_TEXTURE2D(_Tile0, sampler_Tile0, uvTile).r +
                    w.g * SAMPLE_TEXTURE2D(_Tile1, sampler_Tile1, uvTile).r +
                    w.b * SAMPLE_TEXTURE2D(_Tile2, sampler_Tile2, uvTile).r +
                    w.a * SAMPLE_TEXTURE2D(_Tile3, sampler_Tile3, uvTile).r;
                d = lerp(1.0h, d, _DetailStrength);

                half3 albedo = macro * d;

                // Нормаль: вершинная + детальная (TBN, у меша есть тангенсы).
                float2 uvBump = g / _BumpMetres;
                half3 nmap = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvBump), _BumpScale);
                float3 tangentWS = normalize(input.tangentWS.xyz);
                float3 bitangentWS = normalize(cross(input.normalWS, tangentWS) * input.tangentWS.w);
                float3x3 tbn = float3x3(tangentWS, bitangentWS, normalize(input.normalWS));
                half3 normalWS = normalize(mul(nmap, tbn));

                // Освещение URP: главное + доп. источники + ambient. ПРИЁМ ТЕНЕЙ
                // ОТКЛЮЧЁН НАКОНЕЧНО (v1): на наших картах кастеров почти нет
                // (рельеф не кастует, деревья срезаны), а любой теневой путь
                // давал артефакты — тёмные области по силуэтам возвышений и
                // «странные тени» (плейтесты 2026-11-03 ×3: приём-off — чисто).
                // Долг: честный теневой приём после RenderDoc-разбора.
                Light mainLight = GetMainLight();
                half3 lit = LightingLambert(
                    mainLight.color, mainLight.direction, normalWS);

                #if defined(_ADDITIONAL_LIGHTS)
                {
                    uint addCount = GetAdditionalLightsCount();
                    for (uint i = 0u; i < addCount; i++)
                    {
                        Light add = GetAdditionalLight(i, input.positionWS);
                        lit += LightingLambert(
                            add.color * add.shadowAttenuation, add.direction, normalWS) *
                               add.distanceAttenuation;
                    }
                }
                #endif

                half3 ambient = SampleSH(normalWS);
                half3 color = albedo * (lit + ambient);
                color = MixFog(color, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

        // ---------- ShadowCaster (рельеф кастует тени) ----------
        // ДИАГНОСТИКА №2 (плейтест 2026-11-03, артефакт затемнения при
        // приближении жив после ключей качества теней): каст ОТКЛЮЧЁН
        // (вершины выносятся за дальнюю плоскость), приём ОСТАЛСЯ. Ушли
        // артефакты → пишем мусор/acne в карту теней КАСТЕРОМ → лечим bias;
        // остались → сэмпл теней в ForwardLit, копируем канон Literally.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma vertex ShadowVert
            #pragma fragment ShadowFrag
            #pragma multi_compile_instancing
            #define SN_CASTER_OFF 1

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings { float4 positionCS : SV_POSITION; };

            float3 _LightDirection; // ставит SRP перед проходом

            Varyings ShadowVert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                #if SN_CASTER_OFF
                    output.positionCS = float4(0, 0, 3, 1); // за дальней плоскостью — в карту теней не пишемся
                    return output;
                #endif
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.positionCS = TransformWorldToHClip(
                    ApplyShadowBias(positionWS, normalWS, _LightDirection));
                #if UNITY_REVERSED_Z
                    output.positionCS.z = min(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    output.positionCS.z = max(output.positionCS.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return output;
            }

            half4 ShadowFrag(Varyings input) : SV_Target { return 0; }
            ENDHLSL
        }

        // ---------- DepthOnly ----------
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex DepthVert
            #pragma fragment DepthFrag
            #pragma multi_compile_instancing

            struct Attributes { float4 positionOS : POSITION; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings DepthVert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half DepthFrag(Varyings input) : SV_Target { return input.positionCS.z; }
            ENDHLSL
        }

        // ---------- DepthNormals (SSAO/декали движка) ----------
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex DNVert
            #pragma fragment DNFrag
            #pragma multi_compile_instancing

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS   : TEXCOORD0;
            };

            Varyings DNVert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 DNFrag(Varyings input) : SV_Target
            {
                return half4(NormalizeNormalPerPixel(input.normalWS) * 0.5h + 0.5h, 0);
            }
            ENDHLSL
        }
    }
}
