Shader "ThreeUnity/Unlit Line"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1,1,1,1)
        _Color ("Legacy Base Color", Color) = (1,1,1,1)
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0
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
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 position : SV_POSITION;
                fixed4 color : COLOR;
            };

            fixed4 _BaseColor;
            half _Cutoff;
            half _UseVertexColor;

            v2f vert(appdata input)
            {
                v2f output;
                output.position = UnityObjectToClipPos(input.vertex);
                output.color = lerp(fixed4(1, 1, 1, 1), input.color, saturate(_UseVertexColor));
                return output;
            }

            fixed4 frag(v2f input) : SV_Target
            {
                fixed4 surface = _BaseColor * input.color;
                clip(surface.a - _Cutoff);
                return surface;
            }
            ENDCG
        }
    }

    Fallback Off
}
