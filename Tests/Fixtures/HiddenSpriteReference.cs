using System;
using UnityEngine;

namespace BetterSpriteEditor.Tests
{
    public class HiddenSpriteReference : MonoBehaviour
    {
        public Sprite Visible;
        [HideInInspector] public Sprite Hidden;
        [SerializeReference] public Node Graph;

        [Serializable]
        public class Node
        {
            public Sprite Sprite;
            [SerializeReference] public Node Next;
        }
    }
}
