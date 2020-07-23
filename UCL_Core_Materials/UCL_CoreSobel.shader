Shader "Hidden/UCL_CoreSobel"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
		_Dx("Dx", Float) = 1
		_Dy("Dy", Float) = 1

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
			float _Dx;
			float _Dy;
            fixed4 frag (v2f i) : SV_Target
            {
				float4 c = tex2D(_MainTex, i.uv);
				if(c.a == 0) return float4(0,0,0,0);
				//float4 c = tex2D(_MainTex, i.uv);// + _MainTex_TexelSize.xy
				//float3x3 matri;
				float3 c0 = tex2D(_MainTex, i.uv + fixed2(-_MainTex_TexelSize.x, _MainTex_TexelSize.y)).xyz;
				float3 c1 = tex2D(_MainTex, i.uv + fixed2(0, _MainTex_TexelSize.y)).xyz;
				float3 c2 = tex2D(_MainTex, i.uv + fixed2(_MainTex_TexelSize.x, _MainTex_TexelSize.y)).xyz;
				float3 c3 = tex2D(_MainTex, i.uv + fixed2(-_MainTex_TexelSize.x, 0)).xyz;
				//float3 c4 = tex2D(_MainTex, i.uv).xyz;
				float3 c5 = tex2D(_MainTex, i.uv + fixed2(_MainTex_TexelSize.x, 0)).xyz;
				float3 c6 = tex2D(_MainTex, i.uv + fixed2(-_MainTex_TexelSize.x, -_MainTex_TexelSize.y)).xyz;
				float3 c7 = tex2D(_MainTex, i.uv + fixed2(0, -_MainTex_TexelSize.y)).xyz;
				float3 c8 = tex2D(_MainTex, i.uv + fixed2(_MainTex_TexelSize.x, -_MainTex_TexelSize.y)).xyz;

				float3 GX = -_Dy*c0 + _Dy*c2 - _Dx*c3 + _Dx*c5 - _Dy*c6 + _Dy*c8;
				float3 GY = -_Dy*c0 + _Dy*c6 - _Dx*c1 + _Dx*c7 - _Dy*c2 + _Dy*c8;

				float avg = sqrt(GX.x * GX.x + GY.x * GY.x)+ sqrt(GX.y * GX.y + GY.y * GY.y) + sqrt(GX.z * GX.z + GY.z * GY.z);
				//(c.r + c.g + c.b) / 3;
				avg /= 3;
				c.r = c.g = c.b = avg;
				//c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
}
