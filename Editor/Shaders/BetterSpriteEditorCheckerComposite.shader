// Composites the sprite texture over an Aseprite-style checkerboard (16x16 texture pixels,
// so the cells zoom together with the sprite). Baking the checker into the preview blit
// avoids UI Toolkit UV-tiling entirely.
Shader "Hidden/BetterSpriteEditor/CheckerComposite"
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
            float4 _MainTex_TexelSize; // zw = texture width, height

            static const float CELL_SIZE = 16.0;
            static const fixed3 LIGHT = fixed3(0.192, 0.192, 0.192);
            static const fixed3 DARK = fixed3(0.128, 0.128, 0.128);

            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 pixel = i.uv * _MainTex_TexelSize.zw;
                float parity = fmod(floor(pixel.x / CELL_SIZE) + floor(pixel.y / CELL_SIZE), 2.0);
                fixed3 checker = parity < 0.5 ? LIGHT : DARK;
                fixed4 color = tex2D(_MainTex, i.uv);
                return fixed4(lerp(checker, color.rgb, color.a), 1);
            }
            ENDCG
        }
    }
}
