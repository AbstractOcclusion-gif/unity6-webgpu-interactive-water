// WebGL Water - separable Gaussian blur for the caustic texture (Unity 6 / URP port)
// Applied twice from WaterController (horizontal then vertical) to soften caustics.
Shader "WebGLWater/CausticBlur"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Cull Off ZWrite Off ZTest Always

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _BlurDir;   // (offset.x, offset.y, -, -) in texels

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 step = _BlurDir.xy * _MainTex_TexelSize.xy;
                // 9-tap Gaussian
                float w0 = 0.227027;
                float w1 = 0.194594;
                float w2 = 0.121622;
                float w3 = 0.054054;
                float w4 = 0.016216;
                fixed4 c = tex2D(_MainTex, i.uv) * w0;
                c += tex2D(_MainTex, i.uv + step * 1.0) * w1;
                c += tex2D(_MainTex, i.uv - step * 1.0) * w1;
                c += tex2D(_MainTex, i.uv + step * 2.0) * w2;
                c += tex2D(_MainTex, i.uv - step * 2.0) * w2;
                c += tex2D(_MainTex, i.uv + step * 3.0) * w3;
                c += tex2D(_MainTex, i.uv - step * 3.0) * w3;
                c += tex2D(_MainTex, i.uv + step * 4.0) * w4;
                c += tex2D(_MainTex, i.uv - step * 4.0) * w4;
                return c;
            }
            ENDCG
        }
    }
}
