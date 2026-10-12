Shader "Hidden/EPT/Editor/CubemapToCylindrical"
{
    Properties
    {
        [NoScaleOffset] _Cubemap("Cubemap", Cube) = "black" {}
        _Cubemap_HDR("HDR Decode Values", Vector) = (1, 1, 0, 0)
        _Rotation("Rotation", Float) = 0
        _MipLevel("Mip Level", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "UnityCG.cginc"

            samplerCUBE _Cubemap;
            float4 _Cubemap_HDR;
            float _Rotation;
            float _MipLevel;

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            float3 DecodeHdrFullPrecision(float4 encodedColor, float4 decodeInstructions)
            {
                // 与 Unity DecodeHDR 公式一致，但全程使用 float，避免 half 精度截断。
                float alpha = decodeInstructions.w * (encodedColor.a - 1.0) + 1.0;
#if defined(UNITY_COLORSPACE_GAMMA)
                return (decodeInstructions.x * alpha) * encodedColor.rgb;
#else
                return (decodeInstructions.x * pow(alpha, decodeInstructions.y)) * encodedColor.rgb;
#endif
            }

            float4 Frag(Varyings input) : SV_Target
            {
                // 将 2:1 经纬坐标还原为 Unity 左手坐标系中的单位方向。
                float longitude = (input.uv.x - 0.5) * UNITY_TWO_PI + _Rotation;
                float latitude = (input.uv.y - 0.5) * UNITY_PI;
                float cosLatitude = cos(latitude);
                float3 direction = float3(
                    cosLatitude * sin(longitude),
                    sin(latitude),
                    cosLatitude * cos(longitude));

                float4 encodedColor = texCUBElod(_Cubemap, float4(direction, _MipLevel));
                float3 decodedColor = DecodeHdrFullPrecision(encodedColor, _Cubemap_HDR);
                return float4(decodedColor, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
