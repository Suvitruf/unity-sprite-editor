Shader "Hidden/BetterSpriteEditor/AlphaView"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            fixed4 frag(v2f_img i) : SV_Target
            {
                fixed alpha = tex2D(_MainTex, i.uv).a;
                return fixed4(alpha, alpha, alpha, 1);
            }
            ENDCG
        }
    }
}
