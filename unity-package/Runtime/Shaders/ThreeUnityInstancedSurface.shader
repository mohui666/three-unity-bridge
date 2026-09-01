Shader "ThreeUnity/Instanced Surface"
{
    Properties
    {
        _Color ("Base Color", Color) = (1,1,1,1)
        [HideInInspector] _BaseColor ("Base Color Alias", Color) = (1,1,1,1)
        _MainTex ("Base Map", 2D) = "white" {}
        [HideInInspector] _BaseMap ("Base Map Alias", 2D) = "white" {}
        _MetallicGlossMap ("Metallic Smoothness", 2D) = "white" {}
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1
        [HDR] _EmissionColor ("Emission Color", Color) = (0,0,0,1)
        _EmissionMap ("Emission Map", 2D) = "white" {}
        _Metallic ("Metallic", Range(0,1)) = 0
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
        [HideInInspector] _Glossiness ("Smoothness Alias", Range(0,1)) = 0.5
        [HideInInspector] _GlossMapScale ("Smoothness Map Scale", Range(0,1)) = 1
        _Cutoff ("Alpha Cutoff", Range(0,1)) = 0
        _Unlit ("Unlit", Range(0,1)) = 0
        [Toggle] _UseVertexColor ("Use Vertex Color", Float) = 0
        [HideInInspector] _ThreeUnityInstanceColor ("Instance Color", Color) = (1,1,1,1)
        [HideInInspector] _ThreeUnityPbrV8 ("ThreeUnity PBR v8", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
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

        Pass
        {
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 tangent : TANGENT;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 color : COLOR;
                float3 worldNormal : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                float3 worldTangent : TEXCOORD3;
                float3 worldBinormal : TEXCOORD4;
                SHADOW_COORDS(5)
                float2 rawUv : TEXCOORD6;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            UNITY_INSTANCING_BUFFER_START(ThreeUnityPerInstance)
                UNITY_DEFINE_INSTANCED_PROP(float4, _ThreeUnityInstanceColor)
            UNITY_INSTANCING_BUFFER_END(ThreeUnityPerInstance)

            sampler2D _MainTex;
            float4 _MainTex_ST;
            sampler2D _MetallicGlossMap;
            float4 _MetallicGlossMap_ST;
            sampler2D _BumpMap;
            float4 _BumpMap_ST;
            sampler2D _EmissionMap;
            float4 _EmissionMap_ST;
            fixed4 _Color;
            half4 _EmissionColor;
            half _BumpScale;
            half _Metallic;
            half _Smoothness;
            half _Glossiness;
            half _GlossMapScale;
            half _Cutoff;
            half _Unlit;
            half _UseVertexColor;
            half _ThreeUnityPbrV8;

            half3 NormalizeOrZero(half3 value)
            {
                return value * rsqrt(max(dot(value, value), 1e-8));
            }

            v2f vert(appdata input)
            {
                v2f output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                output.pos = UnityObjectToClipPos(input.vertex);
                output.uv = TRANSFORM_TEX(input.uv, _MainTex);
                output.rawUv = input.uv;
                output.color = lerp(fixed4(1, 1, 1, 1), input.color, saturate(_UseVertexColor));
                output.worldNormal = UnityObjectToWorldNormal(input.normal);
                output.worldTangent = UnityObjectToWorldDir(input.tangent.xyz);
                output.worldBinormal = cross(output.worldNormal, output.worldTangent) * input.tangent.w * unity_WorldTransformParams.w;
                output.worldPos = mul(unity_ObjectToWorld, input.vertex).xyz;
                TRANSFER_SHADOW(output);
                return output;
            }

            half4 frag(v2f input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                fixed4 instanceColor = UNITY_ACCESS_INSTANCED_PROP(ThreeUnityPerInstance, _ThreeUnityInstanceColor);
                float2 baseUv = input.uv;
                float2 metallicUv = input.rawUv * _MetallicGlossMap_ST.xy + _MetallicGlossMap_ST.zw;
                float2 normalUv = input.rawUv * _BumpMap_ST.xy + _BumpMap_ST.zw;
                float2 emissionUv = input.rawUv * _EmissionMap_ST.xy + _EmissionMap_ST.zw;
                fixed4 surface = tex2D(_MainTex, baseUv) * _Color * input.color * instanceColor;
                clip(surface.a - _Cutoff);

                if (_ThreeUnityPbrV8 < 0.5)
                {
                    half3 legacyTangentNormal = UnpackNormal(tex2D(_BumpMap, baseUv));
                    legacyTangentNormal.xy *= _BumpScale;
                    half3 legacyNormal = normalize(
                        normalize(input.worldTangent) * legacyTangentNormal.x +
                        normalize(input.worldBinormal) * legacyTangentNormal.y +
                        normalize(input.worldNormal) * legacyTangentNormal.z);
                    half3 legacyLightDirection = normalize(UnityWorldSpaceLightDir(input.worldPos));
                    half3 legacyViewDirection = normalize(UnityWorldSpaceViewDir(input.worldPos));
                    half3 legacyHalfDirection = normalize(legacyLightDirection + legacyViewDirection);
                    UNITY_LIGHT_ATTENUATION(legacyAttenuation, input, input.worldPos);

                    half legacyNDotL = saturate(dot(legacyNormal, legacyLightDirection));
                    half3 legacyAmbient = ShadeSH9(half4(legacyNormal, 1));
                    half3 legacyDirect = _LightColor0.rgb * legacyNDotL * legacyAttenuation;
                    half legacyMetallic = saturate(_Metallic);
                    half legacySmoothness = saturate(max(_Smoothness, _Glossiness));
                    half legacySpecularPower = exp2(4 + legacySmoothness * 6);
                    half legacySpecularStrength = pow(saturate(dot(legacyNormal, legacyHalfDirection)), legacySpecularPower) * legacyNDotL * legacyAttenuation;
                    half3 legacySpecularColor = lerp(half3(0.04, 0.04, 0.04), surface.rgb, legacyMetallic);
                    half3 legacyLit = surface.rgb * (1 - legacyMetallic) * (legacyAmbient + legacyDirect) + _LightColor0.rgb * legacySpecularColor * legacySpecularStrength;
                    half3 legacyEmission = tex2D(_EmissionMap, baseUv).rgb * _EmissionColor.rgb;
                    half3 legacyFinalColor = lerp(legacyLit, surface.rgb, saturate(_Unlit)) + legacyEmission;
                    return half4(legacyFinalColor, surface.a);
                }

                half3 tangentNormal = UnpackNormal(tex2D(_BumpMap, normalUv));
                tangentNormal.xy *= _BumpScale;
                half3 normal = normalize(
                    NormalizeOrZero(input.worldTangent) * tangentNormal.x +
                    NormalizeOrZero(input.worldBinormal) * tangentNormal.y +
                    normalize(input.worldNormal) * tangentNormal.z);
                half3 lightDirection = normalize(UnityWorldSpaceLightDir(input.worldPos));
                half3 viewDirection = normalize(UnityWorldSpaceViewDir(input.worldPos));
                half3 halfDirection = normalize(lightDirection + viewDirection);
                UNITY_LIGHT_ATTENUATION(attenuation, input, input.worldPos);

                half nDotL = saturate(dot(normal, lightDirection));
                half3 ambient = ShadeSH9(half4(normal, 1));
                half3 direct = _LightColor0.rgb * nDotL * attenuation;
                half4 metallicSmoothness = tex2D(_MetallicGlossMap, metallicUv);
                half metallic = saturate(_Metallic * metallicSmoothness.r);
                half smoothness = saturate(max(_Smoothness, _Glossiness) * _GlossMapScale * metallicSmoothness.a);
                half specularPower = exp2(4 + smoothness * 6);
                half specularStrength = pow(saturate(dot(normal, halfDirection)), specularPower) * nDotL * attenuation;
                half3 specularColor = lerp(half3(0.04, 0.04, 0.04), surface.rgb, metallic);
                half3 lit = surface.rgb * (1 - metallic) * (ambient + direct) + _LightColor0.rgb * specularColor * specularStrength;
                half3 emission = tex2D(_EmissionMap, emissionUv).rgb * _EmissionColor.rgb;
                half3 finalColor = lerp(lit, surface.rgb, saturate(_Unlit)) + emission;
                return half4(finalColor, surface.a);
            }
            ENDCG
        }
    }

    Fallback "VertexLit"
}
