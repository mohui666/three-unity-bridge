Shader "ThreeUnity/Billboard"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Color ("Legacy Base Color", Color) = (1,1,1,1)
        _BaseMap ("Base Map", 2D) = "white" {}
        _MainTex ("Legacy Base Map", 2D) = "white" {}
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0
        _PointSize ("Point Size", Float) = 1
        [Toggle] _SizeAttenuation ("Size Attenuation", Float) = 1
        _SpriteRotation ("Sprite Rotation", Float) = 0
        [Enum(Points,0,Sprite,1)] _BillboardMode ("Billboard Mode", Float) = 0
        [Toggle] _UseVertexColor ("Use Vertex Color", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Source Blend", Float) = 1
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Destination Blend", Float) = 0
        [Toggle] _ZWrite ("Z Write", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull [_Cull]
        Blend [_SrcBlend] [_DstBlend]
        ZWrite [_ZWrite]
        ZTest LEqual

        Pass
        {
            CGPROGRAM
            #pragma target 2.0
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                float2 corner : TEXCOORD1;
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
            };

            sampler2D _BaseMap;
            float4 _BaseMap_ST;
            fixed4 _BaseColor;
            half _Cutoff;
            float _PointSize;
            half _SizeAttenuation;
            float _SpriteRotation;
            half _BillboardMode;
            half _UseVertexColor;

            float DeterminantSign()
            {
                float3 basisX = float3(unity_ObjectToWorld._m00, unity_ObjectToWorld._m10, unity_ObjectToWorld._m20);
                float3 basisY = float3(unity_ObjectToWorld._m01, unity_ObjectToWorld._m11, unity_ObjectToWorld._m21);
                float3 basisZ = float3(unity_ObjectToWorld._m02, unity_ObjectToWorld._m12, unity_ObjectToWorld._m22);
                return dot(cross(basisX, basisY), basisZ) < 0 ? -1.0 : 1.0;
            }

            v2f vert(appdata input)
            {
                v2f output;
                float isSprite = step(0.5, _BillboardMode);
                float3 localCenter = lerp(input.vertex.xyz, float3(0, 0, 0), isSprite);
                float4 worldCenter = mul(unity_ObjectToWorld, float4(localCenter, 1));
                float4 viewCenter = mul(UNITY_MATRIX_V, worldCenter);

                if (isSprite > 0.5)
                {
                    float sineRotation;
                    float cosineRotation;
                    sincos(_SpriteRotation, sineRotation, cosineRotation);
                    float2 corner = input.vertex.xy;
                    corner = float2(
                        cosineRotation * corner.x - sineRotation * corner.y,
                        sineRotation * corner.x + cosineRotation * corner.y);
                    float3 basisX = float3(unity_ObjectToWorld._m00, unity_ObjectToWorld._m10, unity_ObjectToWorld._m20);
                    float3 basisY = float3(unity_ObjectToWorld._m01, unity_ObjectToWorld._m11, unity_ObjectToWorld._m21);
                    corner *= float2(length(basisX) * DeterminantSign(), length(basisY));
                    if (_SizeAttenuation < 0.5 && unity_OrthoParams.w < 0.5)
                        corner *= max(0.0001, -viewCenter.z);
                    viewCenter.xy += corner;
                    output.position = mul(UNITY_MATRIX_P, viewCenter);
                }
                else
                {
                    output.position = mul(UNITY_MATRIX_P, viewCenter);
                    float pointPixels = _PointSize;
                    if (_SizeAttenuation > 0.5 && unity_OrthoParams.w < 0.5)
                        pointPixels /= max(0.0001, -viewCenter.z);
                    float2 screenSize = max(_ScreenParams.xy, float2(1, 1));
                    output.position.xy += input.corner * pointPixels * (2.0 / screenSize) * output.position.w;
                }

                output.uv = input.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
                output.color = lerp(fixed4(1, 1, 1, 1), input.color, saturate(_UseVertexColor));
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 surface = tex2D(_BaseMap, input.uv) * _BaseColor * input.color;
                clip(surface.a - _Cutoff);
                return surface;
            }
            ENDCG
        }
    }

    Fallback Off
}
