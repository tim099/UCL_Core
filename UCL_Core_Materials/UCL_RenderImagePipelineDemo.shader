Shader "Hidden/UCL_RenderImagePipelineDemo"
{
    Properties
    {
		_BlendVal("BlendVal", Range(0,1)) = 0.1
        _MainTex ("Texture", 2D) = "white" {}
		_Test("_Test", 2D) = "white" {}
    }
    SubShader
    {
        // No culling or depth
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            sampler2D _MainTex;
			sampler2D _Test;
			float4 _MainTex_TexelSize;
			float _BlendVal;
            fixed4 frag (v2f i) : SV_Target
            {
                fixed4 c = tex2D(_MainTex, i.uv + 10*_MainTex_TexelSize.xy);
				fixed4 c2 = tex2D(_Test, i.uv + 10 * _MainTex_TexelSize.xy);
				fixed4 col = (1 - _BlendVal)*c + _BlendVal * c2;
                // just invert the colors
                //col.rgb = 1 - col.rgb;
                return col;
            }
            ENDCG
        }
    }
}
