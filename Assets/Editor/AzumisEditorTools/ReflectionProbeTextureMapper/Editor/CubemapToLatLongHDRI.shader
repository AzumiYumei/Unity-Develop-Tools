// ================================================================
// Cubemap 转经纬图 HDRI Shader
//
// 文件名称: CubemapToLatLongHDRI.shader
// 版本: 1.0
//
// 描述:
// 将 Cubemap 按经纬展开方式采样输出到 2D RenderTexture。
// 主要给编辑器导出工具使用，不参与运行时业务逻辑。
//
// 修改历史:
// 2026-07-15 - 1.0 初始版本
// ================================================================
Shader "Hidden/Azumi/CubemapToLatLongHDRI"
{
    Properties
    {
        _Cube ("Cubemap", Cube) = "" {}
        _RotationDegrees ("Rotation Degrees", Float) = 0
        _FlipHorizontal ("Flip Horizontal", Float) = 0
        _FlipVertical ("Flip Vertical", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "Queue" = "Overlay"
        }

        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #include "UnityCG.cginc"

            samplerCUBE _Cube;
            float _RotationDegrees;
            float _FlipHorizontal;
            float _FlipVertical;

            struct Attributes
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            /// <summary>
            /// 将全屏四边形顶点转换到裁剪空间，并把原始 UV 传给片元阶段。
            /// </summary>
            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = UnityObjectToClipPos(input.vertex);
                output.uv = input.uv;
                return output;
            }

            /// <summary>
            /// 对采样方向做绕 Y 轴旋转。
            /// 这个旋转主要用于修正不同 Cubemap 来源造成的水平方向偏移。
            /// </summary>
            float3 RotateAroundYAxis(float3 direction, float rotationRadians)
            {
                float sinValue;
                float cosValue;
                sincos(rotationRadians, sinValue, cosValue);

                float rotatedX = direction.x * cosValue - direction.z * sinValue;
                float rotatedZ = direction.x * sinValue + direction.z * cosValue;
                return float3(rotatedX, direction.y, rotatedZ);
            }

            /// <summary>
            /// 按经纬展开公式把 2D UV 映射为单位球方向，再从 Cubemap 采样颜色。
            /// </summary>
            float4 Frag(Varyings input) : SV_Target
            {
                const float PI = 3.14159265359;
                const float TWO_PI = 6.28318530718;

                float2 uv = input.uv;
                if (_FlipHorizontal > 0.5)
                {
                    uv.x = 1.0 - uv.x;
                }

                if (_FlipVertical > 0.5)
                {
                    uv.y = 1.0 - uv.y;
                }

                float phi = (uv.x - 0.5) * TWO_PI;
                float theta = (uv.y - 0.5) * PI;

                float cosTheta = cos(theta);

                float3 direction;
                direction.x = cosTheta * sin(phi);
                direction.y = sin(theta);
                direction.z = cosTheta * cos(phi);
                direction = RotateAroundYAxis(direction, radians(_RotationDegrees));

                return texCUBE(_Cube, direction);
            }
            ENDHLSL
        }
    }
}
