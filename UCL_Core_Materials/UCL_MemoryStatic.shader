// 區塊職責：定義 Whispering Grove 專用的「記憶噪訊」(Memory Static) 特效 Shader
// 物理意義：模擬數位記憶在受損或受干涉時的「慢速衰減」(Slow Decay) 視覺感官，包含隨機噪訊與品紅干涉。
// 數值影響：透過 _StaticIntensity 控制效果強弱，影響 UV 抖動幅度、噪訊透明度與品紅虹彩偏移量。

Shader "UCL/UI/MemoryStatic"
{
    Properties
    {
        // 區塊職責：標準 UI 渲染參數與噪訊特效自定義參數
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        // 核心特效參數
        _StaticIntensity ("Static Intensity", Range(0, 1)) = 0.0     // 特效總強度 (由 C# 控制器驅動)
        _MagentaColor ("Interference Color", Color) = (1, 0, 1, 1)   // 干涉色 (專案標準品紅色)
        _NoiseScale ("Noise Scale", Float) = 50.0                    // 噪訊顆粒大小
        _JitterSpeed ("Jitter Speed", Float) = 15.0                  // UV 抖動頻率

        // UI 模板必要參數
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "Default"
        CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 2.0

            #include "UnityCG.cginc"
            #include "UnityUI.cginc"

            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex   : SV_POSITION;
                fixed4 color    : COLOR;
                float2 texcoord  : TEXCOORD0;
                float4 worldPosition : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            fixed4 _Color;
            fixed4 _TextureSampleAdd;
            float4 _ClipRect;
            float4 _MainTex_ST;

            // 自定義變數
            float _StaticIntensity;
            float4 _MagentaColor;
            float _NoiseScale;
            float _JitterSpeed;

            // 區塊職責：偽隨機雜湊函數
            // 物理意義：生成 0~1 的隨機數值以模擬數位噪訊的隨機性
            float rand(float2 seed)
            {
                return frac(sin(dot(seed, float2(12.9898, 78.233))) * 43758.5453);
            }

            v2f vert(appdata_t v)
            {
                v2f OUT;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                
                // 計算抖動位移 (基於強度與時間)
                float jitter = (rand(float2(_Time.y * _JitterSpeed, 0)) - 0.5) * _StaticIntensity * 0.02;
                
                OUT.worldPosition = v.vertex;
                OUT.worldPosition.x += jitter; // 水平抖動模擬同步訊號不穩
                
                OUT.vertex = UnityObjectToClipPos(OUT.worldPosition);
                OUT.texcoord = TRANSFORM_TEX(v.texcoord, _MainTex);
                OUT.color = v.color * _Color;
                return OUT;
            }

            fixed4 frag(v2f IN) : SV_Target
            {
                // 區塊職責：核心顏色採樣與干涉效果計算
                
                // 1. 計算色差 (Chromatic Aberration) 位移量
                float2 offset = float2(_StaticIntensity * 0.01, 0);
                
                // 2. 採樣主顏色與偏移的品紅干涉顏色
                half4 baseCol = tex2D(_MainTex, IN.texcoord);
                half4 magentaCol = tex2D(_MainTex, IN.texcoord + offset);
                
                // 3. 混合品紅干涉 (模擬數據溢出)
                half4 color = baseCol;
                color.rb += magentaCol.rb * _StaticIntensity * 0.5; // 增強紅藍通道模擬品紅干涉
                
                // 4. 計算數位噪訊 (Static Noise)
                float n = rand(floor(IN.texcoord * _NoiseScale) + _Time.y);
                float noiseAlpha = n * _StaticIntensity * 0.3;
                
                // 5. 疊加噪訊
                color.rgb += (n - 0.5) * _StaticIntensity * 0.2;
                color.a *= (1.0 - noiseAlpha * 0.5); // 噪訊造成的微量透明度波動
                
                color = (color + _TextureSampleAdd) * IN.color;

                #ifdef UNITY_UI_CLIP_RECT
                color.a *= UnityGet2DClipping(IN.worldPosition.xy, _ClipRect);
                #endif

                #ifdef UNITY_UI_ALPHACLIP
                clip (color.a - 0.001);
                #endif

                return color;
            }
        ENDCG
        }
    }
}
