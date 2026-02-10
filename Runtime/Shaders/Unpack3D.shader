Shader "Hidden/Unpack3D"
{
    Properties {
        _VolumeTex("VolumeTex", 3D) = "white" {}
        _SliceZ("SliceZ", Float) = 0
    }
    SubShader {
        Tags { "RenderType"="Opaque" }
        Pass {
            ZTest Always Cull Off ZWrite Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler3D _VolumeTex;
            float _SliceZ;
            fixed4 frag(v2f_img i) : SV_Target {
                float3 uvw = float3(i.uv.xy, _SliceZ);
                return tex3D(_VolumeTex, uvw);
            }
            ENDCG
        }
    }
}
