Shader "Hidden/UCL_CoreSobel"
{
    Properties
    {
        [PerRendererData] _MainTex ("Texture", 2D) = "white" {}
        _LineCol("LineCol", Color) = (1.0, 1.0, 1.0, 1.0)
		_Dx("Dx", Float) = 1
		_Dy("Dy", Float) = 1
		_Weight("Weight", Vector) = (1,1,1,1)
    }
    SubShader
    {
        // No culling or depth
			Cull Off
			Lighting Off
			ZWrite Off
			Blend One OneMinusSrcAlpha
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
			float4 _MainTex_TexelSize;
			float4 _Weight;
			float _Dx;
			float _Dy;
            float4 _LineCol;
            fixed4 frag (v2f i) : SV_Target
            {
				float4 c = tex2D(_MainTex, i.uv);
				if(c.a == 0) return float4(0,0,0,0);
				//float4 c = tex2D(_MainTex, i.uv);// + _MainTex_TexelSize.xy
				//float3x3 matri;
				float4 c0 = tex2D(_MainTex, i.uv + fixed2(-_MainTex_TexelSize.x, _MainTex_TexelSize.y));
				float4 c1 = tex2D(_MainTex, i.uv + fixed2(0, _MainTex_TexelSize.y));
				float4 c2 = tex2D(_MainTex, i.uv + fixed2(_MainTex_TexelSize.x, _MainTex_TexelSize.y));
				float4 c3 = tex2D(_MainTex, i.uv + fixed2(-_MainTex_TexelSize.x, 0));
				//float4 c4 = tex2D(_MainTex, i.uv);
				float4 c5 = tex2D(_MainTex, i.uv + fixed2(_MainTex_TexelSize.x, 0));
				float4 c6 = tex2D(_MainTex, i.uv + fixed2(-_MainTex_TexelSize.x, -_MainTex_TexelSize.y));
				float4 c7 = tex2D(_MainTex, i.uv + fixed2(0, -_MainTex_TexelSize.y));
				float4 c8 = tex2D(_MainTex, i.uv + fixed2(_MainTex_TexelSize.x, -_MainTex_TexelSize.y));

				float4 GX = -_Dy*c0 + _Dy*c2 - _Dx*c3 + _Dx*c5 - _Dy*c6 + _Dy*c8;
				float4 GY = -_Dy*c0 + _Dy*c6 - _Dx*c1 + _Dx*c7 - _Dy*c2 + _Dy*c8;

				float avg = _Weight.x * sqrt(GX.x * GX.x + GY.x * GY.x)+ _Weight.y * sqrt(GX.y * GX.y + GY.y * GY.y)
					+ _Weight.z * sqrt(GX.z * GX.z + GY.z * GY.z) + _Weight.w * sqrt(GX.w * GX.w + GY.w * GY.w);
				//(c.r + c.g + c.b) / 3;
				avg /= (_Weight.x + _Weight.y + _Weight.z + _Weight.w);
                c.r = _LineCol.r * avg;
                c.g = _LineCol.g * avg;
                c.b = _LineCol.b * avg;
				//c.r = c.g = c.b = avg;
				c.a = _LineCol.a * avg;
				//c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
