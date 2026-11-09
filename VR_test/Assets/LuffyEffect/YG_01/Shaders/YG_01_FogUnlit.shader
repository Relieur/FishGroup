Shader "LuffyEffect/YG_01 Fog Unlit"
{
    Properties
    {
        [MainTexture] _FaceTex ("Face Texture", 2D) = "white" {}
        [MainColor] _MainColor ("Main Color", Color) = (0, 0.1607843, 0.2941177, 1)
        _FresnelColor ("Fresnel Color", Color) = (0, 0.7960785, 1, 1)
        _FresnelPower ("Fresnel Power", Range(0.5, 8)) = 2
        _FresnelIntensity ("Fresnel Intensity", Range(0, 4)) = 1
        _FaceIntensity ("Face Texture Intensity", Range(0, 4)) = 1
        _Speed ("UV Scroll Per Second", Vector) = (0, 0.5, 0, 0)
        _PulseSpeed ("Pulse Speed", Float) = 1.5
        _PulseAmount ("Pulse Amount", Range(0, 0.6)) = 0.15
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "UniversalMaterialType" = "Unlit"
            "IgnoreProjector" = "True"
            "Queue" = "Geometry"
        }
        LOD 100

        Pass
        {
            Name "ForwardUnlit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _FaceTex_ST;
                half4 _MainColor;
                half4 _FresnelColor;
                half _FresnelPower;
                half _FresnelIntensity;
                half _FaceIntensity;
                float4 _Speed;
                half _PulseSpeed;
                half _PulseAmount;
            CBUFFER_END

            TEXTURE2D(_FaceTex);
            SAMPLER(sampler_FaceTex);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 positionWS : TEXCOORD2;
                float fogFactor : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS);

                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _FaceTex);
                output.fogFactor = ComputeFogFactor(positionInputs.positionCS.z);

                return output;
            }

            half4 frag (Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                float2 uv = input.uv + _Speed.xy * _Time.y;
                half3 face = SAMPLE_TEXTURE2D(_FaceTex, sampler_FaceTex, uv).rgb;

                half3 normalWS = normalize(input.normalWS);
                half3 viewDirWS = normalize(GetWorldSpaceViewDir(input.positionWS));
                half facing = saturate(dot(normalWS, viewDirWS));
                half rim = pow(1.0 - facing, _FresnelPower);

                half pulse = 1.0 + _PulseAmount * sin(_Time.y * _PulseSpeed);

                half3 color = _MainColor.rgb
                            + face * _FaceIntensity
                            + _FresnelColor.rgb * (rim * _FresnelIntensity * pulse);

                color = MixFog(color, (half)input.fogFactor);

                return half4(color, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }

            ZWrite On
            ColorMask 0

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _FaceTex_ST;
                half4 _MainColor;
                half4 _FresnelColor;
                half _FresnelPower;
                half _FresnelIntensity;
                half _FaceIntensity;
                float4 _Speed;
                half _PulseSpeed;
                half _PulseAmount;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert (Attributes input)
            {
                Varyings output = (Varyings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);

                return output;
            }

            half4 frag (Varyings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

                return 0;
            }
            ENDHLSL
        }
    }

    FallBack "Universal Render Pipeline/Unlit"
}
