// Локальная белая обводка выбора. Добавляется вторым материалом на рендерер предмета (видна только локальному клиенту).
// Два прохода по стенсилу, чтобы получить ровное кольцо вокруг силуэта (обычный inverted hull на коробках даёт «полки» сбоку):
//   1) сам меш пишет в стенсил 1 (цвет не рисует);
//   2) раздутая на _Thickness метров копия рисуется белым только там, где стенсил не 1.
Shader "PropHunt/Outline"
{
    Properties
    {
        _Color ("Color", Color) = (1, 1, 1, 1)
        _Thickness ("Thickness (meters)", Float) = 0.04
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+1" }

        Pass
        {
            Name "OutlineMask"
            Tags { "LightMode" = "SRPDefaultUnlit" }
            ColorMask 0
            ZWrite Off
            ZTest LEqual
            Cull Back
            Stencil { Ref 1 Comp Always Pass Replace }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "OutlineRing"
            Tags { "LightMode" = "UniversalForward" }
            ZWrite Off
            ZTest LEqual
            Cull Back
            Stencil { Ref 1 Comp NotEqual Pass Keep }
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                float _Thickness;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                // раздуваем от центра объекта на постоянную толщину в метрах (выпуклые формы с центром в начале координат)
                float3 posWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 centerWS = TransformObjectToWorld(float3(0, 0, 0));
                posWS += normalize(posWS - centerWS) * _Thickness;
                OUT.positionCS = TransformWorldToHClip(posWS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target { return _Color; }
            ENDHLSL
        }
    }
}
