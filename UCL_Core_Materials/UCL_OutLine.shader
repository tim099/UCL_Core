// Upgrade NOTE: replaced 'mul(UNITY_MATRIX_MVP,*)' with 'UnityObjectToClipPos(*)'

Shader "Hidden/UCL_OutLine"
//reference https://answers.unity.com/questions/1036262/shaders-offset-texture-coordinates-by-a-single-pix.html
//Author _Gkxd 
{
	Properties
	{
		[PerRendererData] _MainTex("Sprite Texture", 2D) = "white" {}
		_Color("Tint", Color) = (1,1,1,1)
		_OutlineColor("Outline", Color) = (1,1,1,1)
		[MaterialToggle] PixelSnap("Pixel snap", Float) = 0
	}

		SubShader
		{
			Tags
			{
				"Queue" = "Transparent"
				"IgnoreProjector" = "True"
				"RenderType" = "Transparent"
				"PreviewType" = "Plane"
				"CanUseSpriteAtlas" = "True"
			}

			Cull Off
			Lighting Off
			ZWrite Off
			Blend One OneMinusSrcAlpha

			Pass
			{
			CGPROGRAM
				#pragma vertex vert
				#pragma fragment frag
				#pragma multi_compile _ PIXELSNAP_ON
				#include "UnityCG.cginc"

				struct appdata_t
				{
					float4 vertex   : POSITION;
					float4 color    : COLOR;
					float2 texcoord : TEXCOORD0;
				};

				struct v2f
				{
					float4 vertex   : SV_POSITION;
					fixed4 color : COLOR;
					half2 texcoord  : TEXCOORD0;
				};

				fixed4 _Color;
				fixed4 _OutlineColor;
				float _TexWidth;
				float _TexHeight;

				v2f vert(appdata_t IN)
				{
					v2f OUT;
					OUT.vertex = UnityObjectToClipPos(IN.vertex);
					OUT.texcoord = IN.texcoord;
					OUT.color = IN.color * _Color;
					#ifdef PIXELSNAP_ON
					OUT.vertex = UnityPixelSnap(OUT.vertex);
					#endif

					return OUT;
				}

				sampler2D _MainTex;
				float4 _MainTex_TexelSize;


				fixed4 frag(v2f IN) : SV_Target
				{
					fixed4 c = tex2D(_MainTex, IN.texcoord) * IN.color;

					if (c.a == 0) {
						return fixed4(0, 0, 0, 0); // Skip outline for transparent pixels
					}

					// Get the colors of the surrounding pixels
					fixed4 up = tex2D(_MainTex, IN.texcoord + fixed2(0, _MainTex_TexelSize.y));
					fixed4 down = tex2D(_MainTex, IN.texcoord - fixed2(0, _MainTex_TexelSize.y));
					fixed4 left = tex2D(_MainTex, IN.texcoord - fixed2(_MainTex_TexelSize.x, 0));
					fixed4 right = tex2D(_MainTex, IN.texcoord + fixed2(_MainTex_TexelSize.x, 0));

					// This method uses an if statement
					if (up.a * down.a * left.a * right.a < 0.1) {
						c.rgb = _OutlineColor.rgb;
						//c.a = 1;
					}

					/* This method doesn't use an if statement, but it won't work for sprites with semi-transparency.

					I prefer the previous method because I don't notice any performace difference between the two.

					float isNotOutline = up.a * down.a * left.a * right.a;
					c.rgb = isNotOutline * c.rgb + (1-isNotOutline) * _OutlineColor;

					*/

					c.rgb *= c.a;
					return c;
				}
			ENDCG
			}
		}
}
