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
        _TileMetres ("Tile period (m)", Float) = 3
        _Tile2Metres ("Tile period 2nd octave (m)", Float) = 48
        _Tile3Metres ("Tile period 3rd octave (m)", Float) = 12
        _DetailStrength ("Detail strength", Range(0, 2)) = 1
        [Header(Scorch (game blast map))] _BlastStrength ("Blast burn strength", Range(0, 1)) = 0
        [Header(Normal detail)] [NoScaleOffset] _BumpMap ("Detail normal", 2D) = "bump" {}
        _BumpMetres ("Normal period (m)", Float) = 1
        _BumpScale ("Normal scale", Range(0, 2)) = 1.6

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
            // ЛОВУКА 1 (v0.154, пробник 2026-11-05): "RenderPipeline" =
            // "UniversalPipeline" у шейдера ИЗ ASSET-БАНДЛА даёт
            // isSupported=false (с тегом варианты SRP-пассов не читаются
            // ни игрой, ни редактором; без тега — True). URP рисует
            // единственный SubShader без тега как обычно.
            // ЛОВУКА 2: LoadFromFile=NULL при живой сборке мода с MP-лобби
            // (v0.154 WIP, SnLobby*) — чинится в той сессии, к бандлу
            // отношения не имеет (пробник без мода: OK).
            // ЛОВУКА 3: batch-сборка без SwitchActiveBuildTarget не пишет
            // объект-контейнер AssetBundle — «not compatible with runtime». = "UniversalPipeline" (v0.154, ловушка):
            // с этим тегом шейдер ИЗ ASSET-БАНДЛА в игре/редакторе даёт
            // isSupported=false (варианты SRP-пассов не выживают при сериализации
            // в бандл); без тега — supported=true, а URP рисует единственный
            // SubShader как обычно (свет/тени — HLSL-функции URP, не тег).
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
        // Глобал игры: воронки/выжженное, ставит BlastManager.CommandSetup
        // (Shader.SetGlobalTexture ID_Global_BlastMap). R8, 0..1 по габариту
        // карты — та же UV-математика, что у макро-маски.
        TEXTURE2D(_Global_BlastMap); SAMPLER(sampler_Global_BlastMap);

        CBUFFER_START(UnityPerMaterial)
            float _MacroScale;
            float _TileMetres;
            float _Tile2Metres;
            float _Tile3Metres;
            float _DetailStrength;
            float _BlastStrength;
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

        /// Бес-синусовый хэш (Dave Hoskins, hash without sine) — NaN-безопасен
        /// на любых координатах, в отличие от frac(sin(dot(p,K)*43758)).
        float hash21(float2 p)
        {
            float3 p3 = frac(p.xyx * 0.1031);
            p3 += dot(p3, p3.yzx + 33.33);
            return frac((p3.x + p3.y) * p3.z);
        }

        // Глобал игры (ShaderGlobalManager.SetDatum): позиция Datum.origin.
        // _WorldSpaceCameraPos НЕ объявлять — уже в UnityInput.hlsl (дубль =
        // «redefinition», шейдер не компилируется; плейтест 2026-11-06).
        float4 _Datum_OriginPosition;
        ENDHLSL

        // ---------- Единственный пасс (SRPDefaultUnlit) ----------
        // ЛОВУКА 4 (v0.154, бисект 2026-11-06): BuildAssetBundles оставляет в
        // шейдере ИЗ БАНДЛА только ПЕРВЫЙ пасс (passCount=1 у всех вариантов,
        // бисект 5 шейдеров). Поэтому пасс ровно ОДИН и БЕЗ тега LightMode:
        // без тега = SRPDefaultUnlit — его URP рендерит в forward, forward+
        // и deferred-фолбэке (ловука deferred: UniversalForward не входит в
        // fallback-список — рельеф был невидим). Тени/глубина-препасс
        // опциональны: каст рельефа off по канону, DepthOnly не критичен.
        Pass
        {
            Name "ForwardLit"

            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]

            HLSLPROGRAM
            #pragma vertex SplatVert
            #pragma fragment SplatFrag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            // КАНОН URP 14: мягкие тени — ЧЕТЫРЕ варианта качества; без них
            // при _SHADOWS_SOFT_MEDIUM остаёмся с жёсткой выборкой — тёмные
            // пятна на близких каскадах (плейтест 2026-11-03).
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            // Приём теней — ЭКСПЕРИМЕНТ за материал-фича (Settings.MapGen
            // .TerrainShadows, default off): каст рельефа остаётся выключен
            // (канон диагностики №2), тени на землю отбрасывают только деревья.
            #pragma shader_feature_local _ _SN_RECEIVE_SHADOWS
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

                // v0.155.1: дистанционное смешение macroW/nearDim УДАЛЕНО.
                // История («фонарь от камеры», плейтест 2026-11-06): вблизи
                // макро-подмес убирался → альбедо = чистое фото ≈ ×2 ярче
                // тёмных зон вдали (фото 0.86 × макро-зоны 0.2..0.5) →
                // клип + bloom = белый «фонарь». Одна формула на все
                // дистанции: albedo = macro(пек-AO/солнце) × фото-тайл.
                // Камера нужна только микрозерну (вес гаснет к 900 м).
                float camDist = distance(input.positionWS, _WorldSpaceCameraPos);

                // Тайлы — период _TileMetres по глобальным метрам + ВТОРАЯ
                // ОКТАВА (~32 м) теми же тайлами: крупный разбив фактуры, без
                // него равнина сливается в однородный ворс (жалоба 2026-11-04).
                // v0.155: тайлы ЦВЕТНЫЕ (фото-страты CC0 Polyhaven, нормализованные
                // к среднему 0.86; серый фолбэк = r=g=b — старая семантика).
                // Три октавы 4/12/48 м, веса 1.0/0.35/0.45 — спектр без ямы 8..32 м.
                float2 uvTile = g / _TileMetres;
                // Анти-повтор: 90°-повороты тайла по клеткам 3 м (k = x+y mod 4)
                float2 tileCell = floor(g / _TileMetres);
                float rotK = fmod(tileCell.x + tileCell.y * 3.0, 4.0);
                float rotA = rotK * 1.5707963;
                float rotS, rotC;
                sincos(rotA, rotS, rotC);
                float2 uvLocal = frac(uvTile) - 0.5;
                uvTile = mad(float2(uvLocal.x * rotC - uvLocal.y * rotS,
                                    uvLocal.x * rotS + uvLocal.y * rotC), 1.0, 0.5);
                half3 d =
                    w.r * SAMPLE_TEXTURE2D(_Tile0, sampler_Tile0, uvTile).rgb +
                    w.g * SAMPLE_TEXTURE2D(_Tile1, sampler_Tile1, uvTile).rgb +
                    w.b * SAMPLE_TEXTURE2D(_Tile2, sampler_Tile2, uvTile).rgb +
                    w.a * SAMPLE_TEXTURE2D(_Tile3, sampler_Tile3, uvTile).rgb;
                d = lerp(half3(1.0h, 1.0h, 1.0h), d, _DetailStrength);
                float2 uvTile3 = g / max(_Tile3Metres, 1e-3f);
                half3 d3 =
                    w.r * SAMPLE_TEXTURE2D(_Tile0, sampler_Tile0, uvTile3).rgb +
                    w.g * SAMPLE_TEXTURE2D(_Tile1, sampler_Tile1, uvTile3).rgb +
                    w.b * SAMPLE_TEXTURE2D(_Tile2, sampler_Tile2, uvTile3).rgb +
                    w.a * SAMPLE_TEXTURE2D(_Tile3, sampler_Tile3, uvTile3).rgb;
                d *= lerp(half3(1.0h, 1.0h, 1.0h), d3, _DetailStrength * 0.35h);
                float2 uvTile2 = g / max(_Tile2Metres, 1e-3f);
                half3 d2 =
                    w.r * SAMPLE_TEXTURE2D(_Tile0, sampler_Tile0, uvTile2).rgb +
                    w.g * SAMPLE_TEXTURE2D(_Tile1, sampler_Tile1, uvTile2).rgb +
                    w.b * SAMPLE_TEXTURE2D(_Tile2, sampler_Tile2, uvTile2).rgb +
                    w.a * SAMPLE_TEXTURE2D(_Tile3, sampler_Tile3, uvTile2).rgb;
                d *= lerp(half3(1.0h, 1.0h, 1.0h), d2, _DetailStrength * 0.45h);


                // v0.155 микрозерно: фото после нормализации мягче шума —
                // живой value-noise от мировых координат даёт гарантированную
                // хай-частоту у камеры (гаснет к 900 м, вдалеке не алиасит).
                // ЛОВУКА (плейтест «фонарь при приближении», 2026-11-06): хэш
                // sin(dot(ni,...)*43758) на мировых координатах ±41 км даёт
                // NaN (у GPU range reduction синуса ломается на аргументах
                // >1e5) → альбедо NaN → буфер+bloom рисуют белое ПЯТНО-фонарь
                // ровно там, где microW>0 (ближе 900 м). Лечение: координаты
                // сворачиваются mod 4096 м до хэша, хэш — БЕС-СИНУСОВЫЙ
                // (Dave Hoskins hash without sine — NaN-безопасен).
                float microW = saturate(1.0 - camDist / 900.0);
                [branch]
                if (microW > 0.01)
                {
                    float2 gq = g - floor(g * (1.0 / 4096.0)) * 4096.0; // [0,4096)
                    float2 np = gq * 2.8;
                    float2 ni = floor(np), nf = frac(np);
                    nf = nf * nf * (3.0 - 2.0 * nf);
                    float ha = hash21(ni);
                    float hb = hash21(ni + float2(1, 0));
                    float hc = hash21(ni + float2(0, 1));
                    float hd = hash21(ni + float2(1, 1));
                    float n1 = lerp(lerp(ha, hb, nf.x), lerp(hc, hd, nf.x), nf.y);
                    float2 np2 = gq * 6.4;
                    float2 ni2 = floor(np2), nf2 = frac(np2);
                    nf2 = nf2 * nf2 * (3.0 - 2.0 * nf2);
                    float ha2 = hash21(ni2);
                    float hb2 = hash21(ni2 + float2(1, 0));
                    float hc2 = hash21(ni2 + float2(0, 1));
                    float hd2 = hash21(ni2 + float2(1, 1));
                    float n2 = lerp(lerp(ha2, hb2, nf2.x), lerp(hc2, hd2, nf2.x), nf2.y);
                    d *= 1.0 + microW * (0.14 * (n1 - 0.5) + 0.09 * (n2 - 0.5));
                }

                // v0.155.1: альбедо = макро (зоны + пек-AO/солнце) × фото-тайл.
                // Одна формула на все дистанции — никаких near/fare-блендов.
                half3 albedo = d * macro;

                // Воронки/выжженное — глобальная карта взрывов игры (воронки от
                // бомб в рантайме + предзапечённые шрамы TerrainDressing).
                // _BlastStrength ставит мод, default 0 (без бандл-обвязки нейтрально).
                if (_BlastStrength > 0.0h)
                {
                    half burn = SAMPLE_TEXTURE2D(
                        _Global_BlastMap, sampler_Global_BlastMap, uvMap).r;
                    albedo *= lerp(1.0h, 1.0h - 0.55h * saturate(burn * 1.4h),
                                   _BlastStrength);
                }

                // Нормаль: вершинная + детальная (TBN, у меша есть тангенсы).
                float2 uvBump = g / _BumpMetres;
                half3 nmap = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uvBump), _BumpScale);
                float3 tangentWS = normalize(input.tangentWS.xyz);
                float3 bitangentWS = normalize(cross(input.normalWS, tangentWS) * input.tangentWS.w);
                float3x3 tbn = float3x3(tangentWS, bitangentWS, normalize(input.normalWS));
                half3 normalWS = normalize(mul(nmap, tbn));

                // Освещение URP: главное + доп. источники + ambient. ПРИЁМ ТЕНЕЙ —
                // эксперимент за _SN_RECEIVE_SHADOWS (v0.154): история v1 — «любой
                // теневой путь давал артефакты, приём-off чисто» (плейтесты
                // 2026-11-03 ×3) — снята каноном диагностики №2 из ShadowCaster:
                // КАСТ так и остаётся за дальней плоскостью, приём включается —
                // в карту теней никто из рельефа не пишет, тени на землю
                // отбрасывают только деревья (кастуют нативно). Артефакты —
                // настройка MapGen.TerrainShadows off.
                #if defined(_SN_RECEIVE_SHADOWS)
                Light mainLight = GetMainLight(
                    TransformWorldToShadowCoord(input.positionWS));
                half3 lit = LightingLambert(
                    mainLight.color, mainLight.direction, normalWS) *
                    mainLight.shadowAttenuation;
                #else
                Light mainLight = GetMainLight();
                half3 lit = LightingLambert(
                    mainLight.color, mainLight.direction, normalWS);
                #endif

                // ДОПОЛНИТЕЛЬНЫЕ ИСТОЧНИКИ УМЕРЕННО: attached-свет камеры и
                // фар бортов вблизи добавлял земле яркость → клип в белое
                // пятно + bloom = «фонарь при приближении» (плейтест
                // 2026-11-06). Рельеф освещает только главный свет + ambient.

                half3 ambient = SampleSH(normalWS) * 0.55h; // гашение: lit+ambient 1.3 клиповал светлые зоны в белое
                half3 color = albedo * (lit + ambient);
                color = MixFog(color, input.fogFactor);
                return half4(color, 1);
            }
            ENDHLSL
        }

    }
}